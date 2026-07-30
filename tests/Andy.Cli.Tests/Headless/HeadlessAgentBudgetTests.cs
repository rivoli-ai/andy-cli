using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class HeadlessAgentBudgetTests
{
    [Fact]
    public void Create_OldConfig_PreservesSingleWindowDefaults()
    {
        var budget = HeadlessAgentBudgetFactory.Create(new HeadlessLimits
        {
            MaxIterations = 40,
            TimeoutSeconds = 300,
        });

        Assert.Equal(40, budget.WindowTurns);
        Assert.Equal(4096, budget.MaxOutputTokens);
        Assert.Null(budget.ContinuationPolicy);
    }

    [Fact]
    public void Create_ConfiguresGlobalTurnsWindowsAndEngineDeadline()
    {
        var budget = HeadlessAgentBudgetFactory.Create(new HeadlessLimits
        {
            MaxIterations = 150,
            TimeoutSeconds = 900,
            MaxOutputTokens = 8192,
            ContinuationWindowIterations = 50,
            EngineTimeoutSeconds = 840,
        });

        Assert.Equal(50, budget.WindowTurns);
        Assert.Equal(8192, budget.MaxOutputTokens);
        Assert.NotNull(budget.ContinuationPolicy);
        Assert.Equal(150, budget.ContinuationPolicy.MaxTotalTurns);
        Assert.Equal(2, budget.ContinuationPolicy.MaxContinuationWindows);
        Assert.Equal(TimeSpan.FromSeconds(840), budget.ContinuationPolicy.MaxElapsedTime);
    }

    [Theory]
    [InlineData("max_turns")]
    [InlineData("max_turns_exceeded")]
    [InlineData("continuation_total_turns_exceeded")]
    [InlineData("continuation_windows_exceeded")]
    [InlineData("continuation_time_exceeded")]
    [InlineData("deadline_exhausted")]
    [InlineData("output_limit_exhausted")]
    public void BudgetStopReasons_MapToTimeout(string stopReason)
    {
        Assert.True(HeadlessAgentRunner.IsBudgetStopReason(stopReason));
    }

    [Fact]
    public void NoProgress_IsAnAgentFailureRatherThanTimeout()
    {
        Assert.False(HeadlessAgentRunner.IsBudgetStopReason("no_progress"));
    }
}
