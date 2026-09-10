using Andy.Model.Llm;

namespace Andy.Cli.Services.Sessions;

/// <summary>Session-owned usage, independent of UI counters and provider/service rebuilds.</summary>
public sealed class SessionUsageTracker
{
    private readonly object _gate = new();
    private SessionUsage? _usage;

    public SessionUsage? GetSnapshot()
    {
        lock (_gate) return _usage;
    }

    public void Restore(SessionUsage? usage)
    {
        lock (_gate) _usage = usage;
    }

    public void Record(LlmUsage usage, string provider, string model)
    {
        var addition = SessionUsage.FromTokenCounts(usage.PromptTokens, usage.CompletionTokens)
            .WithEstimatedCost(provider, model);
        lock (_gate)
        {
            var previous = _usage;
            var combined = (previous ?? SessionUsage.Empty).Add(addition);
            // An incomplete price must not become a complete-looking total merely because
            // a later model has known pricing. Empty initial state has no unpriced work.
            if (previous is { TotalTokens: > 0, EstimatedCostUsd: null }
                || addition is { TotalTokens: > 0, EstimatedCostUsd: null })
                combined = combined with { EstimatedCostUsd = null };
            _usage = combined;
        }
    }
}
