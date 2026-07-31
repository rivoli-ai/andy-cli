using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// A provider's login behaviour. One handler covers the API-key, loopback-OAuth, and
/// device-code flows; which of them a given provider offers comes from its
/// <see cref="ProviderAuthDescriptor"/>. The CLI verb and the TUI talk to this interface only,
/// which is how new providers avoid needing UI changes.
/// </summary>
public interface IProviderAuthHandler
{
    string ProviderId { get; }

    IReadOnlyList<AuthMethodKind> SupportedMethods { get; }

    /// <summary>
    /// Runs a login. Returns the credential to store, or null when the user cancelled at a
    /// prompt. Throws <see cref="OAuthException"/> for protocol failures and
    /// <see cref="OperationCanceledException"/> when the caller's token is cancelled.
    /// </summary>
    Task<StoredCredential?> LoginAsync(AuthMethodKind method, IAuthPrompt prompt, CancellationToken cancellationToken);

    /// <summary>Whether the stored access token is expired or close enough to expiry to renew.</summary>
    bool NeedsRefresh(StoredCredential credential, DateTimeOffset now);

    /// <summary>
    /// Renews an OAuth credential through its refresh token. The renewed record keeps the
    /// original account label so status output stays stable.
    /// </summary>
    Task<StoredCredential> RefreshAsync(StoredCredential credential, CancellationToken cancellationToken);
}

/// <summary>
/// The data-driven handler used for every provider. Behaviour is derived entirely from the
/// descriptor, so adding a provider is a catalog change rather than a code change.
/// </summary>
public sealed class ProviderAuthHandler : IProviderAuthHandler
{
    /// <summary>How long before expiry an access token is considered due for renewal.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(1);

    private readonly ProviderAuthDescriptor _descriptor;
    private readonly IOAuthTokenClient _tokenClient;
    private readonly TimeSpan _loopbackTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ProviderAuthHandler(
        ProviderAuthDescriptor descriptor,
        IOAuthTokenClient? tokenClient = null,
        TimeSpan? loopbackTimeout = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _tokenClient = tokenClient ?? new HttpOAuthTokenClient();
        _loopbackTimeout = loopbackTimeout ?? TimeSpan.FromMinutes(5);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public string ProviderId => _descriptor.ProviderId;

    public IReadOnlyList<AuthMethodKind> SupportedMethods => _descriptor.SupportedMethods;

    public Task<StoredCredential?> LoginAsync(
        AuthMethodKind method,
        IAuthPrompt prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (!SupportedMethods.Contains(method))
        {
            throw new InvalidOperationException(
                $"{_descriptor.DisplayName} does not support the {Describe(method)} login method. "
                + $"Supported: {string.Join(", ", SupportedMethods.Select(Describe))}.");
        }

        return method switch
        {
            AuthMethodKind.ApiKey => LoginWithApiKeyAsync(prompt, cancellationToken),
            AuthMethodKind.OAuthLoopback => LoginWithLoopbackAsync(prompt, cancellationToken),
            AuthMethodKind.OAuthDeviceCode => LoginWithDeviceCodeAsync(prompt, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported login method: {method}.")
        };
    }

    public bool NeedsRefresh(StoredCredential credential, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return credential.Kind == CredentialKind.OAuth
               && !string.IsNullOrEmpty(credential.RefreshToken)
               && credential.IsExpired(now, RefreshSkew);
    }

    public async Task<StoredCredential> RefreshAsync(StoredCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (_descriptor.OAuth is not { IsUsable: true } oauth)
        {
            throw new OAuthException($"{_descriptor.DisplayName} has no OAuth configuration, so its credential cannot be refreshed.");
        }

        if (string.IsNullOrEmpty(credential.RefreshToken))
        {
            throw new OAuthException(
                $"The stored {_descriptor.DisplayName} credential has no refresh token. Run 'andy-cli auth login {_descriptor.ProviderId}' again.");
        }

        var tokens = await _tokenClient
            .RefreshAsync(oauth, credential.RefreshToken, cancellationToken)
            .ConfigureAwait(false);

        return ApplyTokens(credential, tokens, credential.Method ?? "oauth");
    }

    private async Task<StoredCredential?> LoginWithApiKeyAsync(IAuthPrompt prompt, CancellationToken cancellationToken)
    {
        string? apiKey = null;
        string? accountLabel = null;

        foreach (var field in _descriptor.ApiKeyFields)
        {
            var value = await prompt.PromptAsync(field, cancellationToken).ConfigureAwait(false);
            if (value == null && field.Required)
            {
                return null; // cancelled
            }

            if (!field.TryValidate(value, out var error))
            {
                // The message describes the failed rule; it never contains the value.
                throw new AuthValidationException(error);
            }

            var trimmed = value?.Trim();
            if (field.Name == "api_key")
            {
                apiKey = trimmed;
            }
            else if (field.Name == "account_label" && !string.IsNullOrEmpty(trimmed))
            {
                accountLabel = trimmed;
            }
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        return StoredCredential.ForApiKey(apiKey, accountLabel);
    }

    private async Task<StoredCredential?> LoginWithLoopbackAsync(IAuthPrompt prompt, CancellationToken cancellationToken)
    {
        var oauth = _descriptor.OAuth!;

        // Bind the callback before the browser opens, so the redirect URI in the authorization
        // request is always the one actually listening.
        using var listener = LoopbackOAuthListener.Start(oauth.CallbackPort, oauth.CallbackPath);

        var state = OAuthSecurity.CreateRandomToken();
        string? codeVerifier = null;
        string? codeChallenge = null;
        if (oauth.UsePkce)
        {
            codeVerifier = OAuthSecurity.CreateRandomToken();
            codeChallenge = OAuthSecurity.CreateCodeChallenge(codeVerifier);
        }

        var authorizationUrl = OAuthSecurity.BuildAuthorizationUrl(oauth, listener.RedirectUri, state, codeChallenge);
        prompt.PresentUrl($"Approve andy-cli for {_descriptor.DisplayName}", authorizationUrl);
        prompt.Info($"Waiting for the callback on {listener.RedirectUri} (loopback only). Press Ctrl+C to cancel.");

        string code;
        try
        {
            code = await listener
                .WaitForAuthorizationCodeAsync(state, _loopbackTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var tokens = await _tokenClient
            .ExchangeAuthorizationCodeAsync(oauth, code, listener.RedirectUri, codeVerifier, cancellationToken)
            .ConfigureAwait(false);

        return ApplyTokens(null, tokens, "oauth_loopback");
    }

    private async Task<StoredCredential?> LoginWithDeviceCodeAsync(IAuthPrompt prompt, CancellationToken cancellationToken)
    {
        var oauth = _descriptor.OAuth!;

        var authorization = await _tokenClient
            .StartDeviceAuthorizationAsync(oauth, cancellationToken)
            .ConfigureAwait(false);

        prompt.PresentUrl(
            $"Open this page and enter the code {authorization.UserCode}",
            authorization.VerificationUriComplete ?? authorization.VerificationUri);
        prompt.Info($"Waiting for approval (code {authorization.UserCode}). Press Ctrl+C to cancel.");

        var interval = authorization.Interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : authorization.Interval;
        var deadline = _clock() + authorization.ExpiresIn;

        while (true)
        {
            if (_clock() >= deadline)
            {
                throw new OAuthException(
                    $"The device login expired before it was approved. No credential was stored. "
                    + $"Run 'andy-cli auth login {_descriptor.ProviderId}' to try again.");
            }

            try
            {
                await _delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            DevicePollResult poll;
            try
            {
                poll = await _tokenClient
                    .PollDeviceTokenAsync(oauth, authorization.DeviceCode, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            switch (poll.Status)
            {
                case DevicePollStatus.Complete when poll.Tokens != null:
                    return ApplyTokens(null, poll.Tokens, "oauth_device");

                case DevicePollStatus.SlowDown:
                    interval += TimeSpan.FromSeconds(5);
                    continue;

                case DevicePollStatus.Pending:
                    continue;

                default:
                    throw new OAuthException(
                        $"The device login was not completed ({poll.Error ?? "denied"}). No credential was stored.");
            }
        }
    }

    private StoredCredential ApplyTokens(StoredCredential? existing, OAuthTokenResponse tokens, string method)
    {
        var now = _clock();
        return new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = tokens.AccessToken,
            // Authorization servers may omit the refresh token on renewal; keeping the previous
            // one means a refresh never silently strips the ability to renew again.
            RefreshToken = tokens.RefreshToken ?? existing?.RefreshToken,
            ExpiresAtUtc = tokens.ExpiresIn.HasValue ? now + tokens.ExpiresIn.Value : null,
            AccountLabel = tokens.AccountLabel ?? existing?.AccountLabel,
            Method = method,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now
        };
    }

    /// <summary>The stable, user-facing name of a login method (also accepted on the command line).</summary>
    public static string Describe(AuthMethodKind method) => method switch
    {
        AuthMethodKind.ApiKey => "api-key",
        AuthMethodKind.OAuthLoopback => "oauth",
        AuthMethodKind.OAuthDeviceCode => "device-code",
        _ => method.ToString()
    };

    /// <summary>Parses a user-supplied method name; returns null when it is not recognised.</summary>
    public static AuthMethodKind? Parse(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" => null,
        "api-key" or "apikey" or "key" => AuthMethodKind.ApiKey,
        "oauth" or "oauth-loopback" or "browser" => AuthMethodKind.OAuthLoopback,
        "device-code" or "device" => AuthMethodKind.OAuthDeviceCode,
        _ => null
    };
}

/// <summary>
/// Raised when a supplied credential field fails its provider-specific validation. The message
/// names the rule that failed and never echoes the value.
/// </summary>
public sealed class AuthValidationException : Exception
{
    public AuthValidationException(string message) : base(message)
    {
    }
}
