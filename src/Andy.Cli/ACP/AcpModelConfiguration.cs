using Andy.Cli.Services;
using Andy.Llm.Configuration;

namespace Andy.Cli.ACP;

/// <summary>
/// Resolves the model used by ACP startup and applies the same value to the
/// selected provider configuration. Keeping those operations together prevents
/// session metadata/logging from advertising a fallback model that the provider
/// factory never received.
/// </summary>
internal static class AcpModelConfiguration
{
    public static ProviderConfig EnsureProviderConfig(
        LlmOptions options,
        string providerName,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var existing = options.Providers
            .Where(pair =>
                pair.Key.Equals(providerName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Value.Provider, providerName, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(model))
            {
                existing.Model = model;
            }

            return existing;
        }

        var descriptor = ProviderRegistry.Find(providerName);
        var config = new ProviderConfig
        {
            Provider = descriptor?.Id ?? providerName,
            Model = string.IsNullOrWhiteSpace(model) ? descriptor?.DefaultModel ?? providerName : model,
            ApiBase = descriptor == null ? null : ProviderRegistry.GetEndpoint(descriptor.Id),
            ApiKey = descriptor == null
                ? null
                : Environment.GetEnvironmentVariable(descriptor.PrimaryApiKeyEnvVar)
        };
        options.Providers[providerName] = config;
        return config;
    }

    public static string ResolveAndApply(LlmOptions options, string providerName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var providerConfig = options.Providers
            .Where(pair =>
                pair.Key.Equals(providerName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Value.Provider, providerName, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();

        var model = providerConfig?.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            model = options.DefaultModel;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            model = ProviderRegistry.Find(providerName)?.DefaultModel;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            model = providerName;
        }

        if (providerConfig != null && string.IsNullOrWhiteSpace(providerConfig.Model))
        {
            providerConfig.Model = model;
        }

        return model;
    }
}
