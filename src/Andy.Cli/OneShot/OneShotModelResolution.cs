using System.Linq;
using Andy.Cli.Services;

namespace Andy.Cli.OneShot;

// rivoli-ai/andy-cli#279: outcome of resolving `--provider` / `--model` for a
// one-shot run. Either both names are resolved, or Error explains what to set.
public sealed record OneShotModelResolution(string? Provider, string? Model, string? Error)
{
    public static OneShotModelResolution Resolved(string provider, string model)
        => new(provider, model, null);

    public static OneShotModelResolution Failed(string error) => new(null, null, error);
}

// Resolution strategy, injectable so tests never touch ~/.andy or the ambient
// environment.
public delegate OneShotModelResolution OneShotModelResolver(string? requestedProvider, string? requestedModel);

public static class OneShotModelSelection
{
    // Production resolver. Precedence, highest first:
    //   provider: --provider  ->  last interactive selection  ->  credential detection
    //   model:    --model     ->  last model for that provider ->  registry default
    // The registry is the same single source of truth the /model command and the
    // interactive bootstrap read, so a one-shot run picks what the TUI would.
    public static OneShotModelResolution ResolveFromEnvironment(string? requestedProvider, string? requestedModel)
    {
        ModelMemoryService? memory = null;
        try
        {
            memory = new ModelMemoryService();
        }
        catch
        {
            // A read-only or missing home directory must not break `run`.
        }

        var current = SafeGetCurrent(memory);
        var providerName = requestedProvider
            ?? current?.Provider
            ?? new ProviderDetectionService().DetectDefaultProvider();

        if (string.IsNullOrWhiteSpace(providerName))
        {
            return OneShotModelResolution.Failed(
                "No LLM provider is configured. Pass `--provider <name>` or set a provider API key "
                + $"(one of: {string.Join(", ", ProviderRegistry.All.Select(p => p.PrimaryApiKeyEnvVar))}).");
        }

        var descriptor = ProviderRegistry.Find(providerName);
        if (descriptor is null)
        {
            return OneShotModelResolution.Failed(
                $"Unknown provider '{providerName}'. Known providers: "
                + string.Join(", ", ProviderRegistry.All.Select(p => p.Id)) + ".");
        }

        var modelId = requestedModel;
        if (string.IsNullOrWhiteSpace(modelId)
            && current is not null
            && string.Equals(current.Value.Provider, descriptor.Id, StringComparison.OrdinalIgnoreCase))
        {
            modelId = current.Value.Model;
        }
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = SafeGetLastModel(memory, descriptor.Id);
        }
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = descriptor.DefaultModel;
        }

        return OneShotModelResolution.Resolved(descriptor.Id, modelId!);
    }

    private static (string Provider, string Model)? SafeGetCurrent(ModelMemoryService? memory)
    {
        try
        {
            return memory?.GetCurrent();
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGetLastModel(ModelMemoryService? memory, string provider)
    {
        try
        {
            return memory?.GetLastModel(provider);
        }
        catch
        {
            return null;
        }
    }
}
