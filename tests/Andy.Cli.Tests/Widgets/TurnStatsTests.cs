using System;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class TurnStatsTests
{
    [Fact]
    public void Begin_ResetsCountersAndMarksActive()
    {
        var stats = new TurnStats();
        stats.IncrementOperations();
        stats.SetInputTokens(123);
        stats.SetOutputTokens(456);
        stats.End();

        stats.Begin(DateTime.UtcNow);

        Assert.True(stats.IsActive);
        Assert.Equal(0, stats.Operations);
        Assert.Equal(0, stats.InputTokens);
        Assert.Equal(0, stats.OutputTokens);
    }

    [Fact]
    public void End_KeepsFinalValuesButClearsActive()
    {
        var stats = new TurnStats();
        stats.Begin(DateTime.UtcNow);
        stats.IncrementOperations();
        stats.SetOutputTokens(42);

        stats.End();

        Assert.False(stats.IsActive);
        Assert.Equal(1, stats.Operations);
        Assert.Equal(42, stats.OutputTokens);
    }

    [Fact]
    public void Elapsed_IsZeroBeforeBegin()
    {
        var stats = new TurnStats();
        Assert.Equal(TimeSpan.Zero, stats.Elapsed);
    }

    [Fact]
    public void Elapsed_IsPositiveAfterBeginInThePast()
    {
        var stats = new TurnStats();
        stats.Begin(DateTime.UtcNow.AddSeconds(-2));
        Assert.True(stats.Elapsed.TotalSeconds >= 1);
    }

    [Fact]
    public void AddOutputTokens_Accumulates_WhileSetInputReplaces()
    {
        var stats = new TurnStats();
        stats.Begin(DateTime.UtcNow);

        // Output accumulates across round-trips...
        stats.AddOutputTokens(20);
        stats.AddOutputTokens(35);
        // ...while input reflects the latest context size sent.
        stats.SetInputTokens(1000);
        stats.SetInputTokens(1500);

        Assert.Equal(55, stats.OutputTokens);
        Assert.Equal(1500, stats.InputTokens);
    }

    [Fact]
    public async Task IncrementOperations_IsThreadSafe()
    {
        var stats = new TurnStats();
        stats.Begin(DateTime.UtcNow);

        var tasks = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => stats.IncrementOperations()));
        await Task.WhenAll(tasks);

        Assert.Equal(100, stats.Operations);
    }
}
