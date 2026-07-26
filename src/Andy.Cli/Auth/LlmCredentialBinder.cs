using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Llm.Configuration;

namespace Andy.Cli.Auth;

/// <summary>
/// The one place that moves resolved credentials into Andy.Llm's <see cref="LlmOptions"/>.
///
/// Interactive, headless, and ACP mode all call this after
/// <c>ConfigureLlmFromEnvironment()</c>, so a provider configured with <c>andy-cli auth login</c>
/// behaves identically in every mode - which is acceptance criterion "provider switching in
/// interactive, headless, and ACP modes resolves credentials consistently".
///
/// SECURITY: the secret is written to <see cref="ProviderConfig.ApiKey"/> and nowhere else. It is
/// never echoed, never placed in the effective-config output, and an environment-supplied value
/// is never written back to the credential store.
/// </summary>
public static class LlmCredentialBinder
{
    /// <summary>
    /// Applies already-resolved credentials to the options graph. Only providers with a
    /// credential are touched; a provider whose entry already has a key keeps it unless the
    /// environment supplies one (the environment always wins).
    /// </summary>
    public static void Apply(LlmOptions options, IEnumerable<ResolvedCredential> credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);

        foreach (var credential in credentials)
        {
            if (string.IsNullOrEmpty(credential.Secret))
            {
                continue;
            }

            var config = ResolveOrCreateProviderConfig(options, credential.ProviderId);

            // Environment wins outright. Otherwise the stored credential only fills a gap, so a
            // key explicitly written into appsettings.json is still honoured.
            if (credential.Source == CredentialSource.Environment || string.IsNullOrEmpty(config.ApiKey))
            {
                config.ApiKey = credential.Secret;
            }
        }
    }

    /// <summary>
    /// Resolves every known provider and applies the results. Used by the sync service-graph
    /// builders (headless and the one-shot command path), which cannot await.
    /// </summary>
    public static void ApplyResolved(LlmOptions options, ProviderCredentialResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        // The resolver memoizes per provider, so the blocking call here costs at most one store
        // read per provider for the whole process.
        var credentials = resolver.ResolveAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        Apply(options, credentials);
    }

    /// <summary>Async counterpart of <see cref="ApplyResolved"/>.</summary>
    public static async Task ApplyResolvedAsync(
        LlmOptions options,
        ProviderCredentialResolver resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var credentials = await resolver.ResolveAllAsync(cancellationToken).ConfigureAwait(false);
        Apply(options, credentials);
    }

    // Mirrors LlmProviderFactory's own lookup: exact key first, then a match by provider type,
    // creating the entry when neither exists. Kept in sync with the equivalent helper in
    // HeadlessAgentRunner.
    private static ProviderConfig ResolveOrCreateProviderConfig(LlmOptions options, string providerId)
    {
        if (options.Providers.TryGetValue(providerId, out var providerConfig) && providerConfig != null)
        {
            return providerConfig;
        }

        var match = options.Providers.FirstOrDefault(p => string.Equals(
            p.Value?.Provider ?? p.Key.Split('/')[0],
            providerId,
            StringComparison.OrdinalIgnoreCase));

        if (match.Value != null)
        {
            return match.Value;
        }

        var created = new ProviderConfig { Provider = providerId };
        options.Providers[providerId] = created;
        return created;
    }
}
