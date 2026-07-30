using System;
using System.Collections.Generic;
using Andy.Llm.Configuration;

namespace Andy.Cli.Configuration;

/// <summary>
/// Projects the <c>llm</c> section of the layered configuration onto Andy.Llm's
/// own options object.
///
/// Only fields the configuration actually DECLARED are copied. Andy.Llm is
/// configured first from the environment and appsettings.json, and blindly
/// overwriting that with nulls would delete working credentials; the layered
/// configuration is an override, not a replacement.
/// </summary>
public static class LlmOptionsBinder
{
    public static void Apply(LlmOptions options, LlmSection section)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(section);

        if (!string.IsNullOrWhiteSpace(section.DefaultProvider))
        {
            options.DefaultProvider = section.DefaultProvider;
        }

        if (!string.IsNullOrWhiteSpace(section.DefaultModel))
        {
            options.DefaultModel = section.DefaultModel;
        }

        options.Providers ??= new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, declared) in section.Providers)
        {
            if (!options.Providers.TryGetValue(name, out var target))
            {
                target = new ProviderConfig();
                options.Providers[name] = target;
            }

            if (!string.IsNullOrWhiteSpace(declared.Provider))
            {
                target.Provider = declared.Provider;
            }
            if (!string.IsNullOrWhiteSpace(declared.ApiBase))
            {
                target.ApiBase = declared.ApiBase;
            }
            if (!string.IsNullOrEmpty(declared.ApiKey))
            {
                target.ApiKey = declared.ApiKey;
            }
            if (!string.IsNullOrWhiteSpace(declared.Model))
            {
                target.Model = declared.Model;
            }
            target.Enabled = declared.Enabled;
        }
    }
}
