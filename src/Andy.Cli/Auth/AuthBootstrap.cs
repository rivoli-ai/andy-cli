using System;
using System.Threading;
using Andy.Cli.Services;
using Andy.Llm.Configuration;

namespace Andy.Cli.Auth;

/// <summary>
/// One-line entry point each execution mode calls while building its service graph.
///
/// It exists so interactive, headless, and ACP mode share a single resolver instance (and
/// therefore a single credential-store cache), and so each of them needs only one added line
/// rather than a copy of the wiring. Acceptance criterion: "provider switching in interactive,
/// headless, and ACP modes resolves credentials consistently".
///
/// CONFIG SEAM (#280): the store backend is chosen by <see cref="CredentialStoreFactory"/>,
/// which currently reads its override from the environment. When layered configuration lands,
/// that is the only place that needs to change.
/// </summary>
public static class AuthBootstrap
{
    private static readonly object Gate = new();
    private static ProviderCredentialResolver? _resolver;

    /// <summary>
    /// The process-wide resolver. Created on first use against the platform credential store.
    /// </summary>
    public static ProviderCredentialResolver Resolver
    {
        get
        {
            lock (Gate)
            {
                return _resolver ??= ProviderCredentialResolver.CreateDefault();
            }
        }
    }

    /// <summary>Test seam: replaces the shared resolver (and the registry probe it installs).</summary>
    public static void UseResolver(ProviderCredentialResolver? resolver)
    {
        lock (Gate)
        {
            _resolver = resolver;
        }

        ProviderRegistry.StoredCredentialProbe = resolver == null ? null : ProbeStoredCredential;
    }

    /// <summary>
    /// Applies stored credentials to the Andy.Llm options graph and teaches
    /// <see cref="ProviderRegistry"/> to count stored credentials as "available".
    /// Environment variables keep winning and are never persisted.
    /// </summary>
    public static void Configure(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ProviderRegistry.StoredCredentialProbe = ProbeStoredCredential;
        LlmCredentialBinder.ApplyResolved(options, Resolver);
    }

    /// <summary>
    /// Installs the registry probe without touching an options graph. Used by paths that only
    /// need credential detection (for example provider listings).
    /// </summary>
    public static void InstallProbe()
        => ProviderRegistry.StoredCredentialProbe = ProbeStoredCredential;

    private static bool ProbeStoredCredential(string providerId)
    {
        try
        {
            // The resolver memoizes, so repeated detection calls cost one store read per
            // provider for the lifetime of the process.
            var resolved = Resolver.ResolveAsync(providerId, CancellationToken.None).GetAwaiter().GetResult();
            return resolved.HasCredential && resolved.Source != CredentialSource.NotRequired;
        }
        catch (Exception)
        {
            // Credential detection must never be able to break startup; a machine with no
            // credential service simply reports "not configured".
            return false;
        }
    }
}
