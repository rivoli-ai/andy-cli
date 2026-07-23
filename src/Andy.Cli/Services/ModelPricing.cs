using System;
using System.Globalization;

namespace Andy.Cli.Services
{
    /// <summary>USD price per million input/output tokens for a model.</summary>
    public readonly record struct ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);

    /// <summary>
    /// Best-effort static pricing table (USD per million tokens) for the models this CLI
    /// commonly uses. Neither Andy.Llm nor Andy.Engine exposes pricing metadata, so this
    /// mirrors the approach of <see cref="ContextWindow"/>: a conservative, well-known
    /// mapping keyed on stable substrings of the model id, easy to extend by adding rows.
    /// Returns null when the model is unknown, which callers treat as "cost unavailable"
    /// and omit the cost display.
    ///
    /// Prices last verified 2026-07-23. Rows are matched top-to-bottom; put more specific
    /// patterns before more general ones.
    /// </summary>
    public static class ModelPricing
    {
        // (substring pattern, input USD/MTok, output USD/MTok). First match wins.
        private static readonly (string Pattern, ModelPrice Price)[] Table = new[]
        {
            // Anthropic Claude
            ("claude-3-5-haiku", new ModelPrice(0.80m, 4.00m)),
            ("claude-3-haiku",   new ModelPrice(0.25m, 1.25m)),
            ("claude-fable",     new ModelPrice(10.00m, 50.00m)),
            ("claude-mythos",    new ModelPrice(10.00m, 50.00m)),
            ("haiku",            new ModelPrice(1.00m, 5.00m)),   // claude-haiku-4-5 and later
            ("sonnet",           new ModelPrice(3.00m, 15.00m)),  // claude-sonnet-4-6 / sonnet-5
            ("opus",             new ModelPrice(5.00m, 25.00m)),  // claude-opus-4-6 .. 4-8

            // OpenAI GPT / Codex families
            ("gpt-4o-mini",      new ModelPrice(0.15m, 0.60m)),
            ("gpt-4o",           new ModelPrice(2.50m, 10.00m)),
            ("gpt-4.1-nano",     new ModelPrice(0.10m, 0.40m)),
            ("gpt-4.1-mini",     new ModelPrice(0.40m, 1.60m)),
            ("gpt-4.1",          new ModelPrice(2.00m, 8.00m)),
            ("gpt-4-turbo",      new ModelPrice(10.00m, 30.00m)),
            ("gpt-4",            new ModelPrice(30.00m, 60.00m)),
            ("gpt-3.5",          new ModelPrice(0.50m, 1.50m)),
            ("codex-mini",       new ModelPrice(0.25m, 2.00m)),
            ("gpt-5",            new ModelPrice(1.25m, 10.00m)),  // gpt-5 / gpt-5.x codex variants

            // Google Gemini
            ("gemini-2.0-flash", new ModelPrice(0.10m, 0.40m)),
            ("gemini-1.5-flash", new ModelPrice(0.075m, 0.30m)),
            ("gemini-1.5-pro",   new ModelPrice(1.25m, 5.00m)),
            ("gemini",           new ModelPrice(0.10m, 0.40m)),   // conservative flash-tier default

            // Meta Llama (hosted: Groq / Cerebras published rates)
            ("llama-3.3-70b",    new ModelPrice(0.59m, 0.79m)),
            ("llama-3.1-8b",     new ModelPrice(0.05m, 0.08m)),

            // DeepSeek
            ("deepseek-r1",      new ModelPrice(0.55m, 2.19m)),
            ("deepseek",         new ModelPrice(0.27m, 1.10m)),

            // Mistral
            ("codestral",        new ModelPrice(0.30m, 0.90m)),
            ("mistral-large",    new ModelPrice(2.00m, 6.00m)),
        };

        /// <summary>
        /// Resolve the price for the given model, or null when unknown.
        /// Matching is case-insensitive; any "provider/" prefix on the model id is ignored.
        /// Local providers (ollama) are priced at zero since inference runs locally.
        /// </summary>
        public static ModelPrice? GetPrice(string? modelName, string? providerName = null)
        {
            var provider = (providerName ?? string.Empty).ToLowerInvariant();
            if (provider.Contains("ollama") || provider.Contains("local"))
                return new ModelPrice(0m, 0m);

            var model = (modelName ?? string.Empty).ToLowerInvariant();
            var slash = model.LastIndexOf('/');
            if (slash >= 0 && slash < model.Length - 1)
                model = model.Substring(slash + 1);
            if (model.Length == 0)
                return null;

            foreach (var (pattern, price) in Table)
            {
                if (model.Contains(pattern))
                    return price;
            }
            return null;
        }

        /// <summary>
        /// Compute the cost in USD for the given token counts against the model's pricing,
        /// or null when the model is unknown.
        /// </summary>
        public static decimal? ComputeCostUsd(string? modelName, string? providerName, long inputTokens, long outputTokens)
        {
            var price = GetPrice(modelName, providerName);
            if (price == null) return null;
            if (inputTokens < 0) inputTokens = 0;
            if (outputTokens < 0) outputTokens = 0;
            return (inputTokens * price.Value.InputPerMillion
                    + outputTokens * price.Value.OutputPerMillion) / 1_000_000m;
        }

        /// <summary>
        /// Format a USD cost with sensible precision: 4 decimals below $1 (e.g. "$0.0123"),
        /// 2 decimals at $1 and above (e.g. "$1.24").
        /// </summary>
        public static string FormatUsd(decimal cost)
        {
            if (cost < 0) cost = 0;
            string format = cost < 1m ? "0.0000" : "0.00";
            return "$" + cost.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
