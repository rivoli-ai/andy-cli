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
        return provider switch
        {
            "openrouter" => Environment.GetEnvironmentVariable("OPENROUTER_API_BASE") ?? "https://openrouter.ai/api/v1",
            "cerebras" => "https://api.cerebras.ai",
            "openai" => Environment.GetEnvironmentVariable("OPENAI_API_BASE") ?? "https://api.openai.com",
            "anthropic" => "https://api.anthropic.com",
            "gemini" => "https://generativelanguage.googleapis.com",
            "ollama" => Environment.GetEnvironmentVariable("OLLAMA_API_BASE") ?? "http://localhost:11434",
            _ => "unknown"
        };
    }
}
