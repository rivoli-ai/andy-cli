using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;

namespace Andy.Cli.Auth;

/// <summary>
/// The outcome of resolving one provider's credential. Carries the secret for the caller that
/// needs it, plus non-secret metadata for status output. <see cref="ToString"/> is redacted.
/// </summary>
public sealed class ResolvedCredential
{
    public required string ProviderId { get; init; }

    public required CredentialSource Source { get; init; }

    public CredentialKind Kind { get; init; } = CredentialKind.ApiKey;

    /// <summary>The API key or access token. Never logged, never serialized.</summary>
    public string? Secret { get; init; }

    /// <summary>Non-secret account identifier, when the login recorded one.</summary>
    public string? AccountLabel { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>The environment variable that supplied the value, when <see cref="Source"/> is Environment.</summary>
    public string? EnvironmentVariable { get; init; }

    /// <summary>The backend consulted, for status output ("macOS Keychain", ...).</summary>
    public string? StoreName { get; init; }

    /// <summary>Non-secret diagnostic note (for example a failed refresh). Safe to display.</summary>
    public string? Note { get; init; }

    public bool HasCredential => Source == CredentialSource.NotRequired || !string.IsNullOrEmpty(Secret);

    /// <summary>Whether the credential is stored (and therefore removable by logout).</summary>
    public bool IsPersisted => Source is CredentialSource.CredentialStore or CredentialSource.FileFallback;

    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= now;

    /// <summary>A one-line, fully redacted description of where the credential came from.</summary>
    public string DescribeSource() => Source switch
    {
        CredentialSource.Environment => $"environment ({EnvironmentVariable})",
        CredentialSource.CredentialStore => $"credential store ({StoreName})",
        CredentialSource.FileFallback => StoreName ?? "file fallback",
        CredentialSource.NotRequired => "not required (local provider)",
        _ => "not configured"
    };

    /// <summary>Redacted by design.</summary>
    public override string ToString()
        => $"ResolvedCredential({ProviderId}, source={DescribeSource()}, secret={Redaction.Describe(Secret)})";
}

/// <summary>
/// The single credential-resolution path shared by interactive, headless, and ACP mode.
///
/// Precedence, in order:
/// 1. environment variables - highest priority, and never written back to the store;
/// 2. the OS credential store (or the opted-in file fallback);
/// 3. nothing.
///
/// OAuth credentials that are expired or near expiry are renewed here and the renewed record
/// is written back through the same store, so refresh happens once regardless of which mode
/// asked for the credential.
/// </summary>
public sealed class ProviderCredentialResolver
{
    private readonly ICredentialStore _store;
    private readonly Func<string, IProviderAuthHandler?> _handlerFactory;
    private readonly Func<DateTimeOffset> _clock;

    // Reading the OS credential service costs a process spawn on macOS and Linux, and startup
    // resolves every provider to decide which one to default to. The result is memoized for the
    // process lifetime and invalidated explicitly by login/logout, so a cold start pays for at
    // most one lookup per provider.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ResolvedCredential> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _catalogOverlayPath;

    public ProviderCredentialResolver(
        ICredentialStore store,
        Func<string, IProviderAuthHandler?>? handlerFactory = null,
        Func<DateTimeOffset>? clock = null,
        string? catalogOverlayPath = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _handlerFactory = handlerFactory ?? DefaultHandlerFactory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _catalogOverlayPath = catalogOverlayPath;
    }

    /// <summary>The backend this resolver reads from. Non-secret; safe to display.</summary>
    public ICredentialStore Store => _store;

    /// <summary>Resolves a single provider (id or alias).</summary>
    public async Task<ResolvedCredential> ResolveAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var descriptor = _catalogOverlayPath == null
            ? ProviderAuthCatalog.Find(providerId)
            : ProviderAuthCatalog.Find(providerId, _catalogOverlayPath);
        if (descriptor == null)
        {
            return new ResolvedCredential
            {
                ProviderId = providerId,
                Source = CredentialSource.None,
                Note = "Unknown provider."
            };
        }

        return await ResolveAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves every known provider, in registry order.</summary>
    public async Task<IReadOnlyList<ResolvedCredential>> ResolveAllAsync(CancellationToken cancellationToken = default)
    {
        var catalog = _catalogOverlayPath == null
            ? ProviderAuthCatalog.All()
            : ProviderAuthCatalog.All(_catalogOverlayPath);

        var results = new List<ResolvedCredential>();
        foreach (var descriptor in catalog)
        {
            results.Add(await ResolveAsync(descriptor, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Resolves using an already-built descriptor (used by the catalog-driven callers).</summary>
    public async Task<ResolvedCredential> ResolveAsync(
        ProviderAuthDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (_cache.TryGetValue(descriptor.ProviderId, out var cached))
        {
            return cached;
        }

        var resolved = await ResolveUncachedAsync(descriptor, cancellationToken).ConfigureAwait(false);
        _cache[descriptor.ProviderId] = resolved;
        return resolved;
    }

    /// <summary>
    /// Drops the memoized result for a provider (or all providers when null), so the next
    /// resolution re-reads the store. Called after a login or logout changes what is stored.
    /// </summary>
    public void Invalidate(string? providerId = null)
    {
        if (string.IsNullOrEmpty(providerId))
        {
            _cache.Clear();
            return;
        }

        _cache.TryRemove(providerId, out _);
    }

    private async Task<ResolvedCredential> ResolveUncachedAsync(
        ProviderAuthDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        // 1. Environment variables win, always. They are read, never written, and never copied
        //    into the credential store - issue #284 forbids persisting an env-supplied secret.
        foreach (var name in descriptor.EnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                return new ResolvedCredential
                {
                    ProviderId = descriptor.ProviderId,
                    Source = CredentialSource.Environment,
                    Kind = CredentialKind.ApiKey,
                    Secret = value,
                    EnvironmentVariable = name,
                    StoreName = _store.Name
                };
            }
        }

        // 2. The credential store.
        StoredCredential? stored;
        try
        {
            stored = await _store
                .GetAsync(CredentialKeys.ForProvider(descriptor.ProviderId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            // A machine with no credential service must still start when it uses env vars only,
            // so this degrades to "nothing stored" with a visible note instead of throwing.
            return NotConfigured(descriptor, ex.Message);
        }
        catch (CredentialStoreException ex)
        {
            return NotConfigured(descriptor, ex.Message);
        }

        if (stored == null)
        {
            return NotConfigured(descriptor, note: null);
        }

        string? note = null;
        var handler = _handlerFactory(descriptor.ProviderId);
        if (handler != null && handler.NeedsRefresh(stored, _clock()))
        {
            try
            {
                stored = await handler.RefreshAsync(stored, cancellationToken).ConfigureAwait(false);
                await _store
                    .SetAsync(CredentialKeys.ForProvider(descriptor.ProviderId), stored, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OAuthException or CredentialStoreException or CredentialStoreUnavailableException)
            {
                // Keep the (possibly expired) token so the provider can report the real error,
                // and surface a non-secret note explaining what to do.
                note = $"Token refresh failed: {ex.Message}";
            }
        }

        return new ResolvedCredential
        {
            ProviderId = descriptor.ProviderId,
            Source = _store.Source == CredentialSource.None ? CredentialSource.CredentialStore : _store.Source,
            Kind = stored.Kind,
            Secret = stored.Secret,
            AccountLabel = stored.AccountLabel,
            ExpiresAtUtc = stored.ExpiresAtUtc,
            StoreName = _store.Name,
            Note = note
        };
    }

    private ResolvedCredential NotConfigured(ProviderAuthDescriptor descriptor, string? note) => new()
    {
        ProviderId = descriptor.ProviderId,
        Source = descriptor.RequiresCredential ? CredentialSource.None : CredentialSource.NotRequired,
        StoreName = _store.Name,
        Note = note
    };

    private static IProviderAuthHandler? DefaultHandlerFactory(string providerId)
    {
        var descriptor = ProviderAuthCatalog.Find(providerId);
        return descriptor == null ? null : new ProviderAuthHandler(descriptor);
    }

    /// <summary>
    /// Builds the resolver the application uses, wired to the platform credential store.
    /// </summary>
    public static ProviderCredentialResolver CreateDefault()
        => new(CredentialStoreFactory.Create());

    /// <summary>
    /// The set of provider ids that currently have a credential from any source. Used by
    /// startup detection so a provider configured through <c>auth login</c> is treated exactly
    /// like one configured through an environment variable.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListConfiguredProvidersAsync(CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAllAsync(cancellationToken).ConfigureAwait(false);
        return resolved
            .Where(r => r.HasCredential && r.Source != CredentialSource.NotRequired)
            .Select(r => r.ProviderId)
            .ToList();
    }

    /// <summary>
    /// Ordered candidate providers for auto-selection: the registry's detection order, filtered
    /// to those that actually have a credential.
    /// </summary>
    public async Task<string?> DetectDefaultProviderAsync(CancellationToken cancellationToken = default)
    {
        var configured = (await ListConfiguredProvidersAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        return ProviderRegistry.All.FirstOrDefault(p => configured.Contains(p.Id))?.Id;
    }
}
