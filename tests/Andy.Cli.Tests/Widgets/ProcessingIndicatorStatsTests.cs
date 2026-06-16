using System;
using System.Linq;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Covers the live stats suffix rendered on the "thinking" row (elapsed, operations,
/// context tokens) by <see cref="ProcessingIndicatorItem"/>.
/// </summary>
public class ProcessingIndicatorStatsTests
{
    [Fact]
    public void BuildStatsSuffix_NoStats_ShortElapsed_IsEmpty()
    {
        Assert.Equal("", ProcessingIndicatorItem.BuildStatsSuffix(TimeSpan.FromMilliseconds(200), null));
    }

    [Fact]
    public void BuildStatsSuffix_ShowsElapsedOnceAtLeastOneSecond()
    {
        var suffix = ProcessingIndicatorItem.BuildStatsSuffix(TimeSpan.FromSeconds(12.3), null);
        Assert.Contains("12.3s", suffix);
        Assert.StartsWith(" · ", suffix);
    }

    [Fact]
    public void BuildStatsSuffix_IncludesOperationsAndInputOutputTokens()
    {
        var stats = new TurnStats();
        stats.Begin(DateTime.UtcNow);
        stats.IncrementOperations();
        stats.IncrementOperations();
        stats.IncrementOperations();
        stats.SetInputTokens(45_200);
        stats.AddOutputTokens(1_200);

        var suffix = ProcessingIndicatorItem.BuildStatsSuffix(TimeSpan.FromSeconds(5), stats);

        Assert.Contains("3 ops", suffix);
        Assert.Contains("45.2K in", suffix);
        Assert.Contains("1.2K out", suffix);
        Assert.Contains("5.0s", suffix);
    }

    [Fact]
    public void BuildStatsSuffix_OmitsOutputWhenZero()
    {
        var stats = new TurnStats();
        stats.Begin(DateTime.UtcNow);
        stats.SetInputTokens(1000);

        var suffix = ProcessingIndicatorItem.BuildStatsSuffix(TimeSpan.Zero, stats);

        Assert.Contains("1.0K in", suffix);
        Assert.DoesNotContain("out", suffix);
    }

    [Fact]
    public void BuildStatsSuffix_SingularOperation()
    {
        var stats = new TurnStats();
        stats.Begin(DateTime.UtcNow);
        stats.IncrementOperations();

        var suffix = ProcessingIndicatorItem.BuildStatsSuffix(TimeSpan.Zero, stats);

        Assert.Contains("1 op", suffix);
        Assert.DoesNotContain("1 ops", suffix);
    }

    [Fact]
    public void BuildStatsSuffix_OmitsTokensWhenNone()
    {
        var stats = new TurnStats();
        stats.Begin(DateTime.UtcNow);

        var suffix = ProcessingIndicatorItem.BuildStatsSuffix(TimeSpan.Zero, stats);

        Assert.Contains("0 ops", suffix);
        Assert.DoesNotContain("in", suffix);
        Assert.DoesNotContain("out", suffix);
    }

    [Fact]
    public void RenderSlice_DrawsProcessingRowWithStats()
    {
        var stats = new TurnStats();
        stats.Begin(DateTime.UtcNow);
        stats.IncrementOperations();
        stats.IncrementOperations();
        stats.SetInputTokens(12_000);

        var item = new ProcessingIndicatorItem(stats);
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 80, 0, 2, baseDl, builder);
        var dl = builder.Build();

        var text = string.Concat(dl.Ops.OfType<DL.TextRun>().Select(r => r.Content));
        Assert.Contains("Processing request", text);
        Assert.Contains("2 ops", text);
        Assert.Contains("12.0K in", text);
    }
}
