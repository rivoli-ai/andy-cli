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
