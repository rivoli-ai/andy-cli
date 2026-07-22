using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Llm.Configuration;

namespace Andy.Cli.ACP;

/// <summary>A provider/model choice exposed through ACP session configuration.</summary>
public sealed record AcpModelSelection(
    string ValueId,
    string ProviderId,
    string ProviderName,
    string ModelId);

/// <summary>
/// Builds the ACP model picker from the CLI's canonical provider registry. Only
/// providers that are usable in the current environment are offered, while the
/// active startup selection is always retained so session metadata never points
/// at a value that is absent from the picker.
/// </summary>
internal static class AcpModelCatalog
{
    internal const string ConfigId = "model";

    public static IReadOnlyList<AcpModelSelection> Build(
        LlmOptions? options,
        string defaultProvider,
        string defaultModel,
        Func<string, bool>? hasCredentials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModel);

        hasCredentials ??= ProviderRegistry.HasCredentials;
        var canonicalDefault = ProviderRegistry.Resolve(defaultProvider) ?? defaultProvider;
        var selections = new List<AcpModelSelection>();

        foreach (var descriptor in ProviderRegistry.All)
        {
            if (!descriptor.Id.Equals(canonicalDefault, StringComparison.OrdinalIgnoreCase) &&
                !hasCredentials(descriptor.Id))
            {
                continue;
            }

            var configuredModel = FindConfiguredModel(options, descriptor.Id);
            var model = descriptor.Id.Equals(canonicalDefault, StringComparison.OrdinalIgnoreCase)
                ? defaultModel
                : string.IsNullOrWhiteSpace(configuredModel)
                    ? descriptor.DefaultModel
                    : configuredModel;

            selections.Add(CreateSelection(descriptor.Id, descriptor.DisplayName, model));
        }

        if (!selections.Any(selection =>
            selection.ProviderId.Equals(canonicalDefault, StringComparison.OrdinalIgnoreCase) &&
            selection.ModelId.Equals(defaultModel, StringComparison.Ordinal)))
        {
            var descriptor = ProviderRegistry.Find(canonicalDefault);
            selections.Insert(0, CreateSelection(
                canonicalDefault,
                descriptor?.DisplayName ?? canonicalDefault,
                defaultModel));
        }

        return selections;
    }

    private static string? FindConfiguredModel(LlmOptions? options, string providerId)
    {
        if (options == null)
        {
            return null;
        }

        return options.Providers
            .FirstOrDefault(pair =>
                pair.Key.Equals(providerId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Value.Provider, providerId, StringComparison.OrdinalIgnoreCase))
            .Value?.Model;
    }

    private static AcpModelSelection CreateSelection(
        string providerId,
        string providerName,
        string modelId) =>
        new(
            $"{providerId}::{modelId}",
            providerId,
            providerName,
            modelId);
}
