using System;

namespace Andy.Cli.Hosting;

/// <summary>
/// Resolves the display base URL for a given LLM provider. Behaviour-preserving
/// extraction of the private <c>GetProviderUrl</c> helper from <c>Program.cs</c>
/// so the mapping (including its environment-variable overrides) can be unit
/// tested. Used only for the informational "[model] ... [url]" feed line.
/// </summary>
public static class ProviderUrlResolver
{
    public static string Resolve(string provider)
    {
        // Delegate to the single provider source of truth (honors *_API_BASE overrides
        // and provider aliases). Kept as a thin, unit-testable entry point.
        return Andy.Cli.Services.ProviderRegistry.GetEndpoint(provider);
    }
}
