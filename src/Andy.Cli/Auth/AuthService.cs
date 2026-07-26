using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>Outcome of an auth operation. <see cref="Message"/> is always fully redacted.</summary>
public sealed record AuthActionResult(bool Success, string Message);

/// <summary>
/// The provider-auth use cases (list, login, status, logout), independent of any front end.
/// The <c>andy-cli auth</c> verb and the TUI <c>/auth</c> command both drive this, which is what
/// keeps their behaviour and their wording identical.
///
/// SECURITY: nothing this class returns contains secret material. Credentials only ever flow
/// from a prompt into the credential store, or from the store into
/// <see cref="ProviderCredentialResolver"/>.
/// </summary>
public sealed class AuthService
{
    private readonly ICredentialStore _store;
    private readonly ProviderCredentialResolver _resolver;
    private readonly Func<ProviderAuthDescriptor, IProviderAuthHandler> _handlerFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _catalogOverlayPath;

    public AuthService(
        ICredentialStore store,
        ProviderCredentialResolver? resolver = null,
        Func<ProviderAuthDescriptor, IProviderAuthHandler>? handlerFactory = null,
        Func<DateTimeOffset>? clock = null,
        string? catalogOverlayPath = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolver = resolver ?? new ProviderCredentialResolver(store);
        _handlerFactory = handlerFactory ?? (d => new ProviderAuthHandler(d));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _catalogOverlayPath = catalogOverlayPath;
    }

    /// <summary>Builds the service against the platform credential store.</summary>
    public static AuthService CreateDefault()
    {
        var store = CredentialStoreFactory.Create();
        return new AuthService(store, new ProviderCredentialResolver(store));
    }

    private IReadOnlyList<ProviderAuthDescriptor> Catalog =>
        _catalogOverlayPath == null ? ProviderAuthCatalog.All() : ProviderAuthCatalog.All(_catalogOverlayPath);

    private ProviderAuthDescriptor? FindDescriptor(string providerId) =>
        _catalogOverlayPath == null
            ? ProviderAuthCatalog.Find(providerId)
            : ProviderAuthCatalog.Find(providerId, _catalogOverlayPath);

    /// <summary>
    /// Lists every provider with the login methods it supports and whether a credential is
    /// currently resolvable. Fully redacted.
    /// </summary>
    public async Task<string> ListAsync(CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        output.AppendLine("Provider authentication");
        output.AppendLine("-----------------------");
        output.AppendLine($"Credential store: {_store.Name}{(_store.IsAvailable ? string.Empty : " [unavailable]")}");
        output.AppendLine();
        output.AppendLine("PROVIDER      STATUS                          METHODS");

        foreach (var descriptor in Catalog)
        {
            var resolved = await _resolver.ResolveAsync(descriptor, cancellationToken).ConfigureAwait(false);
            var methods = descriptor.SupportedMethods.Count == 0
                ? "none (no credential needed)"
                : string.Join(", ", descriptor.SupportedMethods.Select(ProviderAuthHandler.Describe));

            output.AppendLine(
                $"{Pad(descriptor.ProviderId, 13)} {Pad(ShortStatus(resolved), 31)} {methods}");
        }

        output.AppendLine();
        output.AppendLine("Environment variables always win and are never persisted.");
        output.AppendLine("Run 'andy-cli auth status <provider>' for detail, 'andy-cli auth login <provider>' to sign in.");

        if (!_store.IsAvailable && _store is UnavailableCredentialStore unavailable)
        {
            output.AppendLine();
            output.AppendLine(unavailable.Guidance);
        }

        return output.ToString().TrimEnd();
    }

    /// <summary>
    /// Shows the resolved credential source and account status for one provider, or all of
    /// them. Fully redacted: no part of any secret is emitted.
    /// </summary>
    public async Task<string> StatusAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            var output = new StringBuilder();
            foreach (var descriptor in Catalog)
            {
                output.AppendLine(await DescribeAsync(descriptor, cancellationToken).ConfigureAwait(false));
            }

            return output.ToString().TrimEnd();
        }

        var found = FindDescriptor(providerId);
        if (found == null)
        {
            return UnknownProviderMessage(providerId);
        }

        return await DescribeAsync(found, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DescribeAsync(ProviderAuthDescriptor descriptor, CancellationToken cancellationToken)
    {
        var resolved = await _resolver.ResolveAsync(descriptor, cancellationToken).ConfigureAwait(false);
        var output = new StringBuilder();

        output.AppendLine($"{descriptor.DisplayName} ({descriptor.ProviderId})");
        output.AppendLine($"  credential : {Redaction.Describe(resolved.Secret)}");
        output.AppendLine($"  source     : {resolved.DescribeSource()}");
        output.AppendLine($"  account    : {resolved.AccountLabel ?? "not recorded"}");

        if (resolved.Kind == CredentialKind.OAuth)
        {
            var expiry = resolved.ExpiresAtUtc.HasValue
                ? resolved.ExpiresAtUtc.Value.ToString("u", CultureInfo.InvariantCulture)
                  + (resolved.IsExpired(_clock()) ? " (expired)" : string.Empty)
                : "unknown";
            output.AppendLine($"  expires    : {expiry}");
        }

        if (descriptor.EnvironmentVariables.Count > 0)
        {
            output.AppendLine($"  env vars   : {string.Join(", ", descriptor.EnvironmentVariables)}");
        }

        output.AppendLine($"  methods    : {(descriptor.SupportedMethods.Count == 0
            ? "none (no credential needed)"
            : string.Join(", ", descriptor.SupportedMethods.Select(ProviderAuthHandler.Describe)))}");

        if (!string.IsNullOrEmpty(resolved.Note))
        {
            output.AppendLine($"  note       : {resolved.Note}");
        }

        return output.ToString().TrimEnd();
    }

    /// <summary>
    /// Runs a login for one provider and stores the resulting credential.
    /// </summary>
    public async Task<AuthActionResult> LoginAsync(
        string providerId,
        string? methodName,
        IAuthPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var descriptor = FindDescriptor(providerId);
        if (descriptor == null)
        {
            return new AuthActionResult(false, UnknownProviderMessage(providerId));
        }

        if (descriptor.SupportedMethods.Count == 0)
        {
            return new AuthActionResult(false,
                $"{descriptor.DisplayName} does not need a credential, so there is nothing to log in to.");
        }

        AuthMethodKind method;
        if (string.IsNullOrWhiteSpace(methodName))
        {
            method = descriptor.SupportedMethods[0];
        }
        else
        {
            var parsed = ProviderAuthHandler.Parse(methodName);
            if (parsed == null || !descriptor.SupportedMethods.Contains(parsed.Value))
            {
                return new AuthActionResult(false,
                    $"Unsupported login method '{methodName}' for {descriptor.ProviderId}. "
                    + $"Supported: {string.Join(", ", descriptor.SupportedMethods.Select(ProviderAuthHandler.Describe))}.");
            }

            method = parsed.Value;
        }

        // Fail before collecting a secret we would then be unable to store: prompting for a key
        // and only afterwards admitting there is nowhere to put it is both rude and risky.
        if (!_store.IsAvailable)
        {
            var guidance = _store is UnavailableCredentialStore unavailable
                ? unavailable.Guidance
                : CredentialStoreFactory.NoServiceGuidance();
            return new AuthActionResult(false, guidance);
        }

        if (_store is FileFallbackCredentialStore fileStore)
        {
            prompt.Warn(FileFallbackCredentialStore.PlaintextWarning);
            prompt.Info($"Credential file: {fileStore.FilePath}");
        }

        // An environment variable already supplies this provider. Say so, but continue: the
        // stored credential is still useful once the variable is unset, and it is never
        // overwritten by the environment value.
        var existing = await _resolver.ResolveAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (existing.Source == CredentialSource.Environment)
        {
            prompt.Warn(
                $"Note: {existing.EnvironmentVariable} is set, so it will keep taking priority over "
                + "the credential you are about to store. Unset it to use the stored credential.");
        }

        StoredCredential? credential;
        try
        {
            credential = await _handlerFactory(descriptor)
                .LoginAsync(method, prompt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AuthValidationException ex)
        {
            return new AuthActionResult(false, ex.Message);
        }
        catch (OAuthException ex)
        {
            return new AuthActionResult(false, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new AuthActionResult(false, "Login cancelled. Nothing was stored.");
        }

        if (credential == null)
        {
            return new AuthActionResult(false, "Login cancelled. Nothing was stored.");
        }

        try
        {
            await _store
                .SetAsync(CredentialKeys.ForProvider(descriptor.ProviderId), credential, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            return new AuthActionResult(false, ex.Message);
        }
        catch (CredentialStoreException ex)
        {
            return new AuthActionResult(false, ex.Message);
        }

        _resolver.Invalidate(descriptor.ProviderId);

        var accountSuffix = string.IsNullOrEmpty(credential.AccountLabel)
            ? string.Empty
            : $" for {credential.AccountLabel}";

        return new AuthActionResult(true,
            $"Stored {descriptor.DisplayName} credential{accountSuffix} in {_store.Name}. "
            + $"Run 'andy-cli auth status {descriptor.ProviderId}' to verify.");
    }

    /// <summary>
    /// Removes a provider's stored credential. Because the whole record - API key, refresh
    /// token, and cached access token - lives in a single store entry, the removal is atomic.
    /// </summary>
    public async Task<AuthActionResult> LogoutAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var descriptor = FindDescriptor(providerId);
        if (descriptor == null)
        {
            return new AuthActionResult(false, UnknownProviderMessage(providerId));
        }

        bool removed;
        try
        {
            removed = await _store
                .DeleteAsync(CredentialKeys.ForProvider(descriptor.ProviderId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            return new AuthActionResult(false, ex.Message);
        }
        catch (CredentialStoreException ex)
        {
            return new AuthActionResult(false, ex.Message);
        }

        _resolver.Invalidate(descriptor.ProviderId);

        var message = new StringBuilder();
        message.Append(removed
            ? $"Removed the stored {descriptor.DisplayName} credential (API key, access token, and refresh token) from {_store.Name}."
            : $"No stored {descriptor.DisplayName} credential was found in {_store.Name}; nothing to remove.");

        // Logging out cannot unset a variable in the parent shell, so say what is still active.
        var stillSet = descriptor.EnvironmentVariables
            .Where(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .ToList();
        if (stillSet.Count > 0)
        {
            message.Append(' ');
            message.Append(
                $"{string.Join(" and ", stillSet)} is still set in this environment and will keep supplying a credential; "
                + "unset it to fully sign out.");
        }

        return new AuthActionResult(true, message.ToString());
    }

    private string UnknownProviderMessage(string providerId)
        => $"Unknown provider '{providerId}'. Known providers: {string.Join(", ", Catalog.Select(d => d.ProviderId))}.";

    private static string ShortStatus(ResolvedCredential resolved) => resolved.Source switch
    {
        CredentialSource.Environment => $"configured (env {resolved.EnvironmentVariable})",
        CredentialSource.CredentialStore => "configured (credential store)",
        CredentialSource.FileFallback => "configured (file fallback)",
        CredentialSource.NotRequired => "no credential required",
        _ => "not configured"
    };

    private static string Pad(string value, int width)
        => value.Length >= width ? value : value.PadRight(width);
}
