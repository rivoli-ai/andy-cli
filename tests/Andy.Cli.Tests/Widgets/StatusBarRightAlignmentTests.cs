using System.Linq;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// The status line is split into two zones: the transient status text stays left-aligned,
/// while Model, token usage, cost and turns are anchored to the right edge. Before this,
/// every segment flowed left-to-right, so a changing status string (e.g. "Thinking" with
/// animated dots, or varying lengths) pushed the right-hand readouts back and forth every
/// frame. These tests pin the right-side anchor and the no-overlap behavior.
/// </summary>
public class StatusBarRightAlignmentTests
{
    private static ContextStatusBar Bar(int input = 15_000, int output = 5_000, int max = 200_000,
        int turns = 4, string model = "openai/gpt-4o", string provider = "openai")
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

    private static int FirstXOf(DL.DisplayList dl, string text) =>
        dl.Ops.OfType<DL.TextRun>().First(r => r.Content != null && r.Content.Contains(text)).X;

    private static int RightEdgeOfLastRun(DL.DisplayList dl) =>
        dl.Ops.OfType<DL.TextRun>()
            .Where(r => !string.IsNullOrEmpty(r.Content))
            .Max(r => r.X + r.Content.Length);

    [Fact]
    public void Model_StaysPut_WhenStatusTextChangesLength()
    {
        const int width = 120;
        var bar = Bar();

        bar.SetStatusText("Thinking");
        int xShort = FirstXOf(Render(bar, width), "Model: ");

        bar.SetStatusText("Thinking......................................");
        int xLong = FirstXOf(Render(bar, width), "Model: ");

        bar.SetStatusText("");
        int xEmpty = FirstXOf(Render(bar, width), "Model: ");

        Assert.Equal(xShort, xLong);
        Assert.Equal(xShort, xEmpty);
    }

    [Fact]
    public void TurnsSegment_EndsAtRightMargin()
    {
        const int width = 100;
        var bar = Bar();
        bar.SetStatusText("Working");

        var dl = Render(bar, width);

        // Right group is anchored so its last glyph lands one column from the edge (x margin 1).
        Assert.Equal(width - 1, RightEdgeOfLastRun(dl));
    }

    [Fact]
    public void StatusText_NeverOverlaps_RightGroup_AtAnyWidth()
    {
        // A long status must either be ellipsis-truncated into the left zone or suppressed
        // entirely; it may never run into (or past) the right-anchored group, at any width.
        foreach (var width in new[] { 40, 60, 80, 90, 100, 120, 160 })
        {
            var bar = Bar();
            bar.SetStatusText(new string('x', 200)); // far longer than any bar

            var dl = Render(bar, width);

            var runs = dl.Ops.OfType<DL.TextRun>()
                .Where(r => !string.IsNullOrEmpty(r.Content))
                .OrderBy(r => r.X)
                .ToList();
            if (!runs.Any()) continue;

            int modelX = FirstXOf(dl, "Model: ");
            // Everything drawn left of the right group must end strictly before it (plus
            // the separator gap); nothing may cross into the right-anchored columns.
            foreach (var run in runs.Where(r => r.X < modelX))
            {
                Assert.True(run.X + run.Content.Length <= modelX,
                    $"width={width}: left-zone run '{run.Content}' overlaps the right group at x={modelX}.");
            }
            // If a status sliver survived, it is the leftmost run, starts at the margin and
            // carries the ASCII ellipsis.
            var leftmost = runs.First();
            if (leftmost.Content.Contains('x'))
            {
                Assert.Equal(1, leftmost.X);
                Assert.EndsWith("...", leftmost.Content);
            }
        }
    }

    [Fact]
    public void OversizedStatus_IsSuppressed_WhenNoRoomBesideRightGroup()
    {
        // Right group is ~74 wide; at width 80 the leftover left zone is under 4 columns,
        // so the status is dropped rather than overlapping or shifting the right group.
        var bar = Bar();
        bar.SetStatusText(new string('x', 200));

        var dl = Render(bar, 80);

        var runs = dl.Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrEmpty(r.Content)).ToList();
        Assert.DoesNotContain(runs, r => r.Content.Contains('x'));
        Assert.True(HasRun(dl, "Model: "));
        Assert.Equal(80 - 1, RightEdgeOfLastRun(dl));
    }

    [Fact]
    public void NoStatus_RightGroupStillAnchoredRight()
    {
        const int width = 90;
        var bar = Bar(); // no status text set

        var dl = Render(bar, width);

        Assert.True(HasRun(dl, "Model: "));
        Assert.Equal(width - 1, RightEdgeOfLastRun(dl));
    }

    private static bool HasRun(DL.DisplayList dl, string text) =>
        dl.Ops.Any(op => op is DL.TextRun run && run.Content.Contains(text));
}
