using Andy.Cli.HeadlessConfig;
using Andy.Engine;

namespace Andy.Cli.Headless;

internal sealed record HeadlessAgentBudget(
    int WindowTurns,
    int MaxOutputTokens,
    AgentContinuationPolicy? ContinuationPolicy);

internal static class HeadlessAgentBudgetFactory
{
    private const int DefaultMaxOutputTokens = 4096;

    public static HeadlessAgentBudget Create(HeadlessLimits limits)
    {
        var totalTurns = limits.MaxIterations > 0 ? limits.MaxIterations : 10;
        var windowTurns = limits.ContinuationWindowIterations ?? totalTurns;
        var maxOutputTokens = limits.MaxOutputTokens ?? DefaultMaxOutputTokens;
        var enablesContinuation =
            limits.ContinuationWindowIterations.HasValue ||
            limits.EngineTimeoutSeconds.HasValue;

        if (!enablesContinuation)
        {
            return new HeadlessAgentBudget(
                totalTurns,
                maxOutputTokens,
                ContinuationPolicy: null);
        }

        var continuedWindows = Math.Max(
            1,
            (int)Math.Ceiling((double)totalTurns / windowTurns) - 1);
        var engineTimeoutSeconds = limits.EngineTimeoutSeconds ??
            DeriveEngineTimeoutSeconds(limits.TimeoutSeconds);
        var policy = new AgentContinuationPolicy
        {
            MaxTotalTurns = totalTurns,
            MaxContinuationWindows = continuedWindows,
            MaxElapsedTime = TimeSpan.FromSeconds(engineTimeoutSeconds),
            SoftDeadline = TimeSpan.FromSeconds(
                Math.Max(1, (int)(engineTimeoutSeconds * 0.85))),
            EquivalentCheckpointLimit = 2,
            MaxOutputTokensCeiling = Math.Max(
                maxOutputTokens,
                Math.Min(131_072, maxOutputTokens * 2)),
            RollingToolRoundWindow = 8,
            EquivalentToolRoundLimit = 3,
        };

        return new HeadlessAgentBudget(windowTurns, maxOutputTokens, policy);
    }

    internal static int DeriveEngineTimeoutSeconds(int cliTimeoutSeconds)
    {
        var cleanupMargin = Math.Max(5, (int)Math.Ceiling(cliTimeoutSeconds * 0.03));
        return Math.Max(1, cliTimeoutSeconds - cleanupMargin);
    }
}
