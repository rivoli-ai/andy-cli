using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Andy.Cli.Services.Sessions;

/// <summary>
/// Aggregate token usage and estimated cost for one session (issue #285).
///
/// Cost is DELIBERATELY nullable: null means "no pricing data for this model", which is
/// a different statement from a known cost of zero (a local ollama model really is free).
/// Every consumer must keep the two apart rather than defaulting null to 0.
///
/// Token components are reported side by side rather than summed into one number:
/// providers report cached and reasoning tokens as subsets of the prompt/completion
/// counts, so adding everything up would double count. <see cref="TotalTokens"/> is
/// therefore just input + output.
/// </summary>
public sealed record SessionUsage
{
    public static readonly SessionUsage Empty = new();

    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }

    /// <summary>Reasoning/thinking tokens reported by the provider (a component of output).</summary>
    public long ReasoningTokens { get; init; }

    /// <summary>Prompt tokens served from the provider's cache (a component of input).</summary>
    public long CacheReadTokens { get; init; }

    /// <summary>Prompt tokens written into the provider's cache (a component of input).</summary>
    public long CacheWriteTokens { get; init; }

    /// <summary>Estimated USD cost, or null when the model has no known pricing.</summary>
    public decimal? EstimatedCostUsd { get; init; }

    /// <summary>Input + output. Reasoning and cache counts are components, not extra tokens.</summary>
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>True when nothing at all was recorded (so callers can omit the field entirely).</summary>
    public bool IsEmpty =>
        InputTokens == 0 && OutputTokens == 0 && ReasoningTokens == 0
        && CacheReadTokens == 0 && CacheWriteTokens == 0 && EstimatedCostUsd is null;

    /// <summary>True when a cost could not be estimated because the model's pricing is unknown.</summary>
    public bool PricingUnknown => EstimatedCostUsd is null;

    public static SessionUsage FromTokenCounts(long inputTokens, long outputTokens) => new()
    {
        InputTokens = Math.Max(0, inputTokens),
        OutputTokens = Math.Max(0, outputTokens)
    };

    /// <summary>
    /// Adds two usage records. Costs add only when at least one side is known; when BOTH
    /// sides are unknown the result stays unknown instead of collapsing to zero.
    /// </summary>
    public SessionUsage Add(SessionUsage? other)
    {
        if (other is null)
        {
            return this;
        }

        decimal? cost = EstimatedCostUsd is null && other.EstimatedCostUsd is null
            ? null
            : (EstimatedCostUsd ?? 0m) + (other.EstimatedCostUsd ?? 0m);

        return new SessionUsage
        {
            InputTokens = InputTokens + other.InputTokens,
            OutputTokens = OutputTokens + other.OutputTokens,
            ReasoningTokens = ReasoningTokens + other.ReasoningTokens,
            CacheReadTokens = CacheReadTokens + other.CacheReadTokens,
            CacheWriteTokens = CacheWriteTokens + other.CacheWriteTokens,
            EstimatedCostUsd = cost
        };
    }

    /// <summary>
    /// Returns a copy with the estimated cost recomputed from the static pricing table.
    /// Leaves the cost null when the model is not in the table.
    /// </summary>
    public SessionUsage WithEstimatedCost(string? provider, string? model) => this with
    {
        EstimatedCostUsd = ModelPricing.ComputeCostUsd(model, provider, InputTokens, OutputTokens)
    };

    public JsonObject ToJson()
    {
        var node = new JsonObject
        {
            ["inputTokens"] = InputTokens,
            ["outputTokens"] = OutputTokens,
            ["reasoningTokens"] = ReasoningTokens,
            ["cacheReadTokens"] = CacheReadTokens,
            ["cacheWriteTokens"] = CacheWriteTokens
        };
        // Written only when known: an absent property and a null one both mean
        // "pricing unknown", never "free".
        if (EstimatedCostUsd is { } cost)
        {
            node["estimatedCostUsd"] = cost;
        }
        return node;
    }

    public static SessionUsage? FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usage = new SessionUsage
        {
            InputTokens = SessionJson.ReadLong(element, "inputTokens"),
            OutputTokens = SessionJson.ReadLong(element, "outputTokens"),
            ReasoningTokens = SessionJson.ReadLong(element, "reasoningTokens"),
            CacheReadTokens = SessionJson.ReadLong(element, "cacheReadTokens"),
            CacheWriteTokens = SessionJson.ReadLong(element, "cacheWriteTokens"),
            EstimatedCostUsd = SessionJson.ReadNullableDecimal(element, "estimatedCostUsd")
        };
        return usage.IsEmpty ? null : usage;
    }

    /// <summary>"$0.0123" when known, or the given placeholder when pricing is unknown.</summary>
    public string FormatCost(string unknownText = "unknown (no pricing data)") =>
        EstimatedCostUsd is { } cost ? ModelPricing.FormatUsd(cost) : unknownText;

    public static string FormatTokens(long tokens) =>
        tokens.ToString("N0", CultureInfo.InvariantCulture);
}
