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
    ///
    /// Covered models (last verified 2026-06-22):
    ///   Anthropic:  Claude (200K)
    ///   Google:     Gemini (1M)
    ///   OpenAI:     GPT-4o, GPT-4.1, GPT-4-Turbo, GPT-4 (128K / 8K),
    ///               GPT-3.5 (16K), o1/o3/o4 reasoning family (128K),
    ///               Codex variants via gpt-5.x-codex (128K)
    ///   Qwen:       Qwen2.5, Qwen3, Qwen-Long (128K–1M)
    ///   DeepSeek:   DeepSeek-V2/V3, DeepSeek-R1, DeepSeek-Coder (128K)
    ///   Mistral:    Mistral Large/Nemo (128K), Mistral Medium/Small (32K), Codestral (256K)
    ///   Meta:       Llama (8K safe floor via Cerebras / Ollama)
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

            // Qwen models: 128K for most (Qwen2.5, Qwen3, Coder variants),
            // 1M for long-context variants (Qwen-Long, Qwen 1M).
            if (provider.Contains("qwen") || model.Contains("qwen"))
            {
                if (model.Contains("long") || model.Contains("-1m"))
                    return 1_000_000;
                return 128_000;
            }

            // DeepSeek: 128K across V2, V3, R1, and Coder variants.
            if (provider.Contains("deepseek") || model.Contains("deepseek"))
                return 128_000;

            // Mistral: varies by model tier.
            if (provider.Contains("mistral") || model.Contains("mistral") || model.Contains("codestral"))
            {
                if (model.Contains("codestral"))
                    return 256_000;
                if (model.Contains("large") || model.Contains("nemo"))
                    return 128_000;
                // Medium, Small, and other variants: conservative 32K.
                return 32_000;
            }

            // OpenAI reasoning models (o1, o3, o4 families): 128K.
            // Require a boundary after the prefix to avoid false-matching unrelated names
            // (e.g. "ollama" would not match since the char after "o" is "l", not a digit).
            if (IsReasoningModel(model))
                return 128_000;

            // OpenAI GPT families.
            if (model.Contains("gpt-4o") || model.Contains("gpt-4.1") ||
                model.Contains("gpt-4-turbo") || model.Contains("/o1") || model.Contains("/o3"))
                return 128_000;
            if (model.Contains("gpt-5"))
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

        /// <summary>
        /// Returns true if <paramref name="model"/> looks like an OpenAI reasoning model
        /// (o1, o3, o4 and their variants). The match requires a digit after the leading "o"
        /// and a valid boundary character (end-of-string, '-', '.', or digit) after the number,
        /// so names like "ollama" or "openai" are not falsely matched.
        /// </summary>
        private static bool IsReasoningModel(string model)
        {
            if (model.Length < 2 || model[0] != 'o')
                return false;

            char second = model[1];
            // Must be a digit: o1, o3, o4, etc.
            if (second < '0' || second > '9')
                return false;

            if (model.Length == 2)
                return true; // exact "o1", "o3", etc.

            char third = model[2];
            // Valid boundary: end of major version segment.
            // e.g. "o1", "o1-mini", "o1.1", "o30", "o4-mini"
            return third == '-' || third == '.' || (third >= '0' && third <= '9');
        }
    }
}
