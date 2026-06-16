using System;

namespace Andy.Cli.Services
{
    /// <summary>
    /// Best-effort lookup of a model's maximum context window (in tokens) from its name and
    /// provider. The engine consumes the LLM as a package and does not surface
    /// <c>ModelInfo.MaxTokens</c> through <see cref="SimpleAssistantService"/>, so this provides a
    /// conservative, well-known mapping for the providers this CLI uses. Returns 0 when the window
    /// is unknown, which the status bar treats as "context capacity unavailable" and degrades
    /// gracefully (no percentage, no "/max").
    /// </summary>
    public static class ContextWindow
    {
        /// <summary>
        /// Resolve the maximum context window for the given model/provider, or 0 if unknown.
        /// Matching is case-insensitive and based on stable substrings of the model id.
        /// </summary>
        public static int GetMaxTokens(string? modelName, string? providerName)
        {
            var model = (modelName ?? string.Empty).ToLowerInvariant();
            var provider = (providerName ?? string.Empty).ToLowerInvariant();

            // Anthropic Claude: 200K across the current generation.
            if (provider.Contains("anthropic") || model.Contains("claude"))
                return 200_000;

            // Google Gemini: 1M+ context; use a conservative 1M.
            if (provider.Contains("gemini") || provider.Contains("google") || model.Contains("gemini"))
                return 1_000_000;

            // OpenAI families.
            if (model.Contains("gpt-4o") || model.Contains("gpt-4.1") ||
                model.Contains("gpt-4-turbo") || model.StartsWith("o1") || model.StartsWith("o3") ||
                model.Contains("/o1") || model.Contains("/o3"))
                return 128_000;
            if (model.Contains("gpt-4"))
                return 8_192;
            if (model.Contains("gpt-3.5"))
                return 16_385;

            // Meta Llama (e.g. via Cerebras / Ollama): 8K is the safe common floor.
            if (model.Contains("llama"))
                return 8_192;

            return 0;
        }
    }
}
