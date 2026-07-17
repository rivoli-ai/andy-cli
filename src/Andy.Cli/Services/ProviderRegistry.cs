using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Services;

/// <summary>
/// Describes a single LLM provider: its canonical id, aliases, the environment
/// variables that supply credentials/endpoints, the default endpoint and model,
/// detection priority, and capability flags.
/// </summary>
public sealed class ProviderDescriptor
{
    /// <summary>Canonical provider id (lower case). This is the value passed to the LLM provider factory.</summary>
    public required string Id { get; init; }

    /// <summary>Human-friendly display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Alternate names that resolve to <see cref="Id"/> (e.g. "gemini" -> "google").</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Environment variables that must all be present for the provider to be considered
    /// available via credential detection. Empty for local providers (e.g. Ollama).
    /// </summary>
    public IReadOnlyList<string> ApiKeyEnvVars { get; init; } = Array.Empty<string>();

    /// <summary>Optional environment variable that overrides the endpoint (e.g. OPENAI_API_BASE).</summary>
    public string? ApiBaseEnvVar { get; init; }

    /// <summary>Default endpoint used when no override env var is set.</summary>
    public required string DefaultEndpoint { get; init; }

    /// <summary>Default model id used when nothing else is configured.</summary>
    public required string DefaultModel { get; init; }

    /// <summary>Detection priority; lower wins when several providers are available.</summary>
    public int DetectionPriority { get; init; }

    /// <summary>Whether the provider requires an API key. Local providers (Ollama) do not.</summary>
    public bool RequiresApiKey { get; init; } = true;

    /// <summary>Whether the provider runs locally and is detected by probing rather than by env var.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Whether the provider exposes a model-listing API that /model list/refresh can query.</summary>
    public bool SupportsModelListing { get; init; } = true;

    /// <summary>
    /// Whether the provider imposes a small tool-count limit and therefore needs the reduced
    /// tool set (used to surface a note when switching to it).
    /// </summary>
    public bool LimitsToolCount { get; init; }

    /// <summary>The primary API-key env var name (used in user-facing messages).</summary>
    public string PrimaryApiKeyEnvVar => ApiKeyEnvVars.Count > 0 ? ApiKeyEnvVars[0] : $"{Id.ToUpperInvariant()}_API_KEY";
}

/// <summary>
/// Single source of truth for the set of LLM providers the CLI supports.
/// Detection (<see cref="ProviderDetectionService"/>), the /model command, startup
/// bootstrap, and user-facing help all read from this registry so the advertised
/// provider set cannot drift between them.
/// </summary>
public static class ProviderRegistry
{
    private static readonly ProviderDescriptor[] _providers =
    {
        new ProviderDescriptor
        {
            Id = "openrouter",
            DisplayName = "OpenRouter",
            ApiKeyEnvVars = new[] { "OPENROUTER_API_KEY" },
            ApiBaseEnvVar = "OPENROUTER_API_BASE",
            DefaultEndpoint = "https://openrouter.ai/api/v1",
            DefaultModel = "xiaomi/mimo-v2.5",
            DetectionPriority = 0 // Preferred when its key is present (default Mimo-v2.5 setup)
        },
        new ProviderDescriptor
        {
            Id = "openai",
            DisplayName = "OpenAI",
            ApiKeyEnvVars = new[] { "OPENAI_API_KEY" },
            ApiBaseEnvVar = "OPENAI_API_BASE",
            DefaultEndpoint = "https://api.openai.com",
            DefaultModel = "gpt-4o",
            DetectionPriority = 1 // Most reliable fallback
        },
        new ProviderDescriptor
        {
            Id = "anthropic",
            DisplayName = "Anthropic",
            ApiKeyEnvVars = new[] { "ANTHROPIC_API_KEY" },
            DefaultEndpoint = "https://api.anthropic.com",
            DefaultModel = "claude-3-5-haiku-20241022",
            DetectionPriority = 2
        },
        new ProviderDescriptor
        {
            Id = "cerebras",
            DisplayName = "Cerebras",
            ApiKeyEnvVars = new[] { "CEREBRAS_API_KEY" },
            DefaultEndpoint = "https://api.cerebras.ai",
            DefaultModel = "llama-3.3-70b",
            DetectionPriority = 3,
            LimitsToolCount = true // Limited to a few essential tools to prevent API errors
        },
        new ProviderDescriptor
        {
            Id = "groq",
            DisplayName = "Groq",
            ApiKeyEnvVars = new[] { "GROQ_API_KEY" },
            DefaultEndpoint = "https://api.groq.com/openai/v1",
            DefaultModel = "llama-3.3-70b-versatile",
            DetectionPriority = 4
        },
        new ProviderDescriptor
        {
            Id = "google",
            DisplayName = "Google Gemini",
            Aliases = new[] { "gemini" },
            ApiKeyEnvVars = new[] { "GOOGLE_API_KEY" },
            DefaultEndpoint = "https://generativelanguage.googleapis.com",
            DefaultModel = "gemini-2.0-flash-exp",
            DetectionPriority = 5
        },
        new ProviderDescriptor
        {
            Id = "ollama",
            DisplayName = "Ollama",
            ApiKeyEnvVars = Array.Empty<string>(),
            ApiBaseEnvVar = "OLLAMA_API_BASE",
            DefaultEndpoint = "http://localhost:11434",
            DefaultModel = "llama3.2",
            DetectionPriority = 6,
            RequiresApiKey = false,
            IsLocal = true
        }
    };

    /// <summary>All providers, ordered by detection priority.</summary>
    public static IReadOnlyList<ProviderDescriptor> All { get; } =
        _providers.OrderBy(p => p.DetectionPriority).ToArray();

    /// <summary>The canonical ids of all providers, ordered by detection priority.</summary>
    public static IReadOnlyList<string> Ids { get; } = All.Select(p => p.Id).ToArray();

    /// <summary>
    /// Resolves a provider id or alias (case-insensitive) to its descriptor, or null if unknown.
    /// </summary>
    public static ProviderDescriptor? Find(string? idOrAlias)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias))
            return null;

        return _providers.FirstOrDefault(p =>
            p.Id.Equals(idOrAlias, StringComparison.OrdinalIgnoreCase) ||
            p.Aliases.Any(a => a.Equals(idOrAlias, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Returns the canonical id for an id or alias, or null if unknown.</summary>
    public static string? Resolve(string? idOrAlias) => Find(idOrAlias)?.Id;

    /// <summary>Whether the given id or alias maps to a known provider.</summary>
    public static bool IsKnown(string? idOrAlias) => Find(idOrAlias) != null;

    /// <summary>Gets the effective endpoint for a provider, honoring its override env var.</summary>
    public static string GetEndpoint(string? idOrAlias)
    {
        var descriptor = Find(idOrAlias);
        if (descriptor == null)
            return "unknown";

        if (!string.IsNullOrEmpty(descriptor.ApiBaseEnvVar))
        {
            var overrideValue = Environment.GetEnvironmentVariable(descriptor.ApiBaseEnvVar);
            if (!string.IsNullOrEmpty(overrideValue))
                return overrideValue;
        }

        return descriptor.DefaultEndpoint;
    }

    /// <summary>The primary API-key env var name for a provider (best-effort for unknown ids).</summary>
    public static string GetApiKeyEnvVar(string? idOrAlias)
    {
        var descriptor = Find(idOrAlias);
        if (descriptor != null)
            return descriptor.PrimaryApiKeyEnvVar;

        return $"{(idOrAlias ?? string.Empty).ToUpperInvariant()}_API_KEY";
    }

    /// <summary>
    /// Whether the provider's credentials are present in the environment. Local providers
    /// without required env vars are treated as always available (their reachability is
    /// checked elsewhere).
    /// </summary>
    public static bool HasCredentials(string? idOrAlias)
    {
        var descriptor = Find(idOrAlias);
        if (descriptor == null)
            return false;

        if (!descriptor.RequiresApiKey || descriptor.ApiKeyEnvVars.Count == 0)
            return true;

        return descriptor.ApiKeyEnvVars.All(v =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)));
    }
}
