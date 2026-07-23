using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Tests for the collapsed single status line: segment composition, cost display,
/// width-based segment dropping, and truncation.
/// </summary>
public class StatusLineTests
{
    private static ContextStatusBar Bar(int input, int output, int max, int turns,
        string model = "", string provider = "")
    {
        var bar = new ContextStatusBar();
        bar.Update(input, output, max, turns);
        bar.SetModelInfo(model, provider);
        return bar;
    }

    private static DL.DisplayList Render(ContextStatusBar bar, int width = 120, int height = 30)
    {
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        bar.Render((width, height), baseDl, builder);
        return builder.Build();
    }

    private static bool HasRunContaining(DL.DisplayList dl, string text) =>
        dl.Ops.Any(op => op is DL.TextRun run && run.Content.Contains(text));

    // --- Segment composition -----------------------------------------------------

    [Fact]
    public void BuildSegments_FullState_ContainsAllSegmentsInDisplayOrder()
    {
        var bar = Bar(100_000, 10_000, 200_000, 4, "gpt-4o", "openai");
        bar.SetStatusText("Thinking");

        var segments = bar.BuildSegments();
        var kinds = segments.Select(s => s.Kind).ToList();

        Assert.Equal(new[]
        {
            StatusSegmentKind.Status,
            StatusSegmentKind.Model,
            StatusSegmentKind.Usage,
            StatusSegmentKind.Cost,
            StatusSegmentKind.Turns,
        }, kinds);
    }

    [Fact]
    public void BuildSegments_CostComputedFromTokensAndPricing()
    {
        // gpt-4o: 100K in * $2.50/M + 10K out * $10.00/M = $0.25 + $0.10 = $0.35
        var bar = Bar(100_000, 10_000, 200_000, 4, "gpt-4o", "openai");
        var cost = bar.BuildSegments().Single(s => s.Kind == StatusSegmentKind.Cost);
        Assert.Equal("$0.3500", cost.Text);
    }

    [Fact]
    public void BuildSegments_UnknownModel_OmitsCostSegment()
    {
        var bar = Bar(100_000, 10_000, 200_000, 4, "mystery-model", "acme");
        Assert.DoesNotContain(bar.BuildSegments(), s => s.Kind == StatusSegmentKind.Cost);
    }

    [Fact]
    public void BuildSegments_ZeroTokens_OmitsCostSegment()
    {
        var bar = Bar(0, 0, 200_000, 0, "gpt-4o", "openai");
        Assert.DoesNotContain(bar.BuildSegments(), s => s.Kind == StatusSegmentKind.Cost);
    }

    [Fact]
    public void BuildSegments_NoStatusText_OmitsStatusSegment()
    {
        var bar = Bar(1000, 100, 200_000, 1, "gpt-4o", "openai");
        Assert.DoesNotContain(bar.BuildSegments(), s => s.Kind == StatusSegmentKind.Status);
    }

    [Fact]
    public void BuildSegments_ActiveTurn_ReplacesUsageWithLiveTokensAndPerRequestCost()
    {
        var stats = new TurnStats();
        stats.Begin(System.DateTime.UtcNow);
        stats.SetInputTokens(100_000);
        stats.AddOutputTokens(10_000);

        var bar = Bar(100_000, 10_000, 200_000, 4, "gpt-4o", "openai");
        bar.SetLiveStats(stats);

        var segments = bar.BuildSegments();
        Assert.DoesNotContain(segments, s => s.Kind == StatusSegmentKind.Usage);
        var live = segments.Single(s => s.Kind == StatusSegmentKind.Live);
        Assert.Equal("100.0K in -> 10.0K out ($0.3500)", live.Text);
    }

    [Fact]
    public void BuildSegments_ActiveTurn_UnknownModel_LiveTokensWithoutCost()
    {
        var stats = new TurnStats();
        stats.Begin(System.DateTime.UtcNow);
        stats.SetInputTokens(2000);

        var bar = Bar(5000, 500, 200_000, 2, "mystery-model", "acme");
        bar.SetLiveStats(stats);

        var live = bar.BuildSegments().Single(s => s.Kind == StatusSegmentKind.Live);
        Assert.Equal("2.0K in", live.Text);
    }

    // --- Width fitting (drop least important first) --------------------------------

    private static StatusSegment Seg(StatusSegmentKind kind, string text, int priority) =>
        new(kind, text, priority);

    [Fact]
    public void FitSegments_AllFit_KeepsEverything()
    {
        var segments = new List<StatusSegment>
        {
            Seg(StatusSegmentKind.Model, "Model: gpt-4o", 1),
            Seg(StatusSegmentKind.Usage, "20.0K / 200.0K", 2),
            Seg(StatusSegmentKind.Turns, "4 turns", 5),
        };
        var fitted = ContextStatusBar.FitSegments(segments, 200);
        Assert.Equal(3, fitted.Count);
    }

    [Fact]
    public void FitSegments_Overflow_DropsHighestPriorityFirst()
    {
        var segments = new List<StatusSegment>
        {
            Seg(StatusSegmentKind.Model, "Model: gpt-4o", 1),   // 13 chars
            Seg(StatusSegmentKind.Usage, "20.0K / 200.0K", 2),  // 14 chars
            Seg(StatusSegmentKind.Cost, "$0.3500", 3),          // 7 chars
            Seg(StatusSegmentKind.Turns, "~36 turns left", 5),  // 14 chars
        };
        // Full width: 13 + 14 + 7 + 14 + 3*3 = 57. Fit into 45: turns (priority 5) drops.
        var fitted = ContextStatusBar.FitSegments(segments, 45);
        Assert.DoesNotContain(fitted, s => s.Kind == StatusSegmentKind.Turns);
        Assert.Contains(fitted, s => s.Kind == StatusSegmentKind.Cost);
        Assert.Contains(fitted, s => s.Kind == StatusSegmentKind.Usage);
        Assert.Contains(fitted, s => s.Kind == StatusSegmentKind.Model);
    }

    [Fact]
    public void FitSegments_SevereOverflow_KeepsModelLast()
    {
        var segments = new List<StatusSegment>
        {
            Seg(StatusSegmentKind.Model, "Model: gpt-4o", 1),
            Seg(StatusSegmentKind.Usage, "20.0K / 200.0K", 2),
            Seg(StatusSegmentKind.Cost, "$0.3500", 3),
            Seg(StatusSegmentKind.Turns, "~36 turns left", 5),
        };
        var fitted = ContextStatusBar.FitSegments(segments, 15);
        var only = Assert.Single(fitted);
        Assert.Equal(StatusSegmentKind.Model, only.Kind);
    }

    [Fact]
    public void FitSegments_SingleSegmentTooLong_TruncatesWithAsciiEllipsis()
    {
        var segments = new List<StatusSegment>
        {
            Seg(StatusSegmentKind.Model, "Model: some-very-long-model-name", 1),
        };
        var fitted = ContextStatusBar.FitSegments(segments, 12);
        var only = Assert.Single(fitted);
        Assert.Equal(12, only.Text.Length);
        Assert.EndsWith("...", only.Text);
    }

    [Fact]
    public void FitSegments_WidthTooSmallForAnything_ReturnsEmpty()
    {
        var segments = new List<StatusSegment>
        {
            Seg(StatusSegmentKind.Model, "Model: gpt-4o", 1),
        };
        Assert.Empty(ContextStatusBar.FitSegments(segments, 2));
    }

    // --- Rendering -----------------------------------------------------------------

    [Fact]
    public void Render_SingleLine_ShowsCostAndSeparators()
    {
        var bar = Bar(100_000, 10_000, 200_000, 4, "gpt-4o", "openai");
        var dl = Render(bar);

        Assert.True(HasRunContaining(dl, "$0.3500"));
        Assert.True(HasRunContaining(dl, " | "));
    }

    [Fact]
    public void Render_UnknownModel_OmitsCost()
    {
        var bar = Bar(100_000, 10_000, 200_000, 4, "mystery-model", "acme");
        var dl = Render(bar);
        Assert.False(HasRunContaining(dl, "$"));
    }

    [Fact]
    public void Render_StatusText_AppearsOnTheLine()
    {
        var bar = Bar(1000, 100, 200_000, 1, "gpt-4o", "openai");
        bar.SetStatusText("Thinking", animated: false);
        var dl = Render(bar);
        Assert.True(HasRunContaining(dl, "Thinking"));
    }

    [Fact]
    public void Render_EverythingOnBottomRow()
    {
        var bar = Bar(100_000, 10_000, 200_000, 4, "gpt-4o", "openai");
        bar.SetStatusText("Ready");
        var dl = Render(bar, width: 120, height: 30);

        foreach (var op in dl.Ops)
        {
            if (op is DL.TextRun run)
            {
                Assert.Equal(29, run.Y); // height - 1: a single collapsed row
            }
        }
    }

    [Fact]
    public void Render_NarrowTerminal_DropsTurnsBeforeModel()
    {
        var bar = Bar(100_000, 10_000, 200_000, 4, "gpt-4o", "openai");
        // Wide enough for model + usage + cost but not turns.
        var dl = Render(bar, width: 60);

        Assert.True(HasRunContaining(dl, "gpt-4o"));
        Assert.False(HasRunContaining(dl, "turns"));
    }
}
