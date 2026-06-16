using System.Linq;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Guards the invariant that broke for a long time: the feed lays out in display rows using
/// <see cref="MarkdownRendererItem.MeasureLineCount"/> and renders with RenderSlice, so the
/// measured row count MUST equal the rows the real Andy.Tui renderer actually draws. When measure
/// over-counts, the surplus rows render blank at the bottom of every response. These tests drive
/// the REAL renderer (not a duplicated heuristic) so they fail if the two paths ever diverge again.
/// </summary>
public class MarkdownRendererItemMeasureTests
{
    private static (int measured, int maxRow, int rowCount) Render(string md, int width)
    {
        var item = new MarkdownRendererItem(md);
        int measured = item.MeasureLineCount(width);

        var baseDl = new DL.DisplayListBuilder().Build();
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, measured, baseDl, b);

        var ys = b.Build().Ops
            .OfType<DL.TextRun>()
            .Where(t => !string.IsNullOrEmpty(t.Content))
            .Select(t => t.Y)
            .ToList();

        int maxRow = ys.Count == 0 ? -1 : ys.Max();
        return (measured, maxRow, ys.Distinct().Count());
    }

    [Fact]
    public void Measure_EqualsRenderedRowSpan_ForHeadingsAndLists()
    {
        // Shape that previously over-counted: headings + numbered/bulleted lists + trailing text.
        var md = "# Current Open Issues\n\n**High Priority:**\n1. #44 - Epic\n2. #53 - Capability\n\n"
               + "**Recent Activity:**\n- #114 - Context compaction\n- #98 - Markdown tables\n\n"
               + "Would you like me to provide more details about any specific issue?";

        var (measured, maxRow, _) = Render(md, 90);

        Assert.True(maxRow >= 0, "expected the renderer to draw some text");
        // No reserved-but-empty rows below the content: measured == last drawn row + 1.
        Assert.Equal(maxRow + 1, measured);
    }

    [Fact]
    public void Measure_DoesNotInflate_ForShortList()
    {
        var (measured, _, _) = Render("- a\n- b\n- c", 80);
        // Three short items; a handful of rows at most, never the old inflated estimate.
        Assert.InRange(measured, 3, 8);
    }

    [Fact]
    public void Measure_TrailingNewlinesInInput_ProduceNoBlankRows()
    {
        var (measured, maxRow, _) = Render("Here is some text.\n\nMore text here.\n\n\n\n\n", 80);
        Assert.Equal(maxRow + 1, measured);
    }

    [Fact]
    public void RenderSlice_WindowIsClipped_ToRequestedHeight()
    {
        var md = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"Line number {i}"));
        var item = new MarkdownRendererItem(md);
        int total = item.MeasureLineCount(80);
        Assert.True(total >= 5);

        var baseDl = new DL.DisplayListBuilder().Build();
        var b = new DL.DisplayListBuilder();
        // Ask for a 3-row window starting at display row 2.
        item.RenderSlice(0, 100, 80, startLine: 2, maxLines: 3, baseDl, b);
        var ops = b.Build().Ops;

        // A clip bounding the window must be present, and no glyph may appear above the window top.
        Assert.Contains(ops, op => op is DL.ClipPush);
        var textYs = ops.OfType<DL.TextRun>().Where(t => !string.IsNullOrEmpty(t.Content)).Select(t => t.Y).ToList();
        Assert.All(textYs, y => Assert.True(y >= 100 - 2, $"row {y} should not be above the shifted origin"));
    }
}
