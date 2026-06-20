using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Guards the two table rendering bugs that were fixed:
/// (1) MeasureLineCount over-counted relative to what was drawn, leaving a phantom blank line
///     around every table; and
/// (2) the table had no vertical column separators / side borders.
/// These tests drive the real <see cref="TableItem.RenderSlice"/> so they fail if either path
/// regresses.
/// </summary>
public class TableItemTests
{
    private static TableItem MakeRealisticTable()
    {
        var headers = new List<string> { "Name", "Age", "City" };
        var rows = new List<string[]>
        {
            new[] { "Alice", "30", "New York" },
            new[] { "Bob", "25", "Los Angeles" },
            new[] { "Carol", "41", "Chicago" },
            new[] { "Dave", "7", "San Francisco Bay Area" },
        };
        return new TableItem(headers, rows);
    }

    private static List<DL.TextRun> RenderRuns(TableItem item, int width, out int measured)
    {
        measured = item.MeasureLineCount(width);
        var baseDl = new DL.DisplayListBuilder().Build();
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, measured, baseDl, b);
        return b.Build().Ops
            .OfType<DL.TextRun>()
            .Where(t => !string.IsNullOrEmpty(t.Content))
            .ToList();
    }

    [Fact]
    public void Measure_EqualsDistinctRenderedRows_NoPhantomLine()
    {
        var item = MakeRealisticTable();
        var runs = RenderRuns(item, 80, out int measured);

        int distinctRows = runs.Select(r => r.Y).Distinct().Count();

        // The feed reserves exactly `measured` display rows; RenderSlice must draw glyphs on
        // exactly that many distinct rows (no fewer => no reserved-but-blank phantom row).
        Assert.Equal(measured, distinctRows);

        // Sanity: top border + header + separator + 4 data rows + bottom border = 8.
        Assert.Equal(8, measured);

        // Rows are contiguous from the top with no gap.
        int maxRow = runs.Max(r => r.Y);
        Assert.Equal(measured - 1, maxRow);
    }

    [Fact]
    public void Render_ContainsVerticalSeparatorGlyphs()
    {
        var item = MakeRealisticTable();
        var runs = RenderRuns(item, 80, out _);

        // Vertical column dividers / side borders must be present.
        bool hasVertical = runs.Any(r => r.Content.Contains('│'));
        Assert.True(hasVertical, "expected vertical separator glyphs in rendered table");

        // The header row (Y=1) must have at least 4 vertical glyphs for a 3-column table
        // (left edge + 2 inner dividers + right edge).
        var headerVerticals = runs.Where(r => r.Y == 1 && r.Content == "│").Count();
        Assert.True(headerVerticals >= 4, $"expected >= 4 vertical glyphs on header row, found {headerVerticals}");
    }

    [Fact]
    public void Render_ContainsBoxCorners_TopAndBottom()
    {
        var item = MakeRealisticTable();
        var runs = RenderRuns(item, 80, out int measured);

        var topRow = runs.Where(r => r.Y == 0).Select(r => r.Content);
        var botRow = runs.Where(r => r.Y == measured - 1).Select(r => r.Content);

        Assert.Contains(topRow, c => c.Contains('┌') && c.Contains('┬') && c.Contains('┐'));
        Assert.Contains(botRow, c => c.Contains('└') && c.Contains('┴') && c.Contains('┘'));
    }

    [Fact]
    public void Render_NarrowWidth_StillMeasuresEqualToRenderedRows()
    {
        // Force the proportional shrink path with a width far smaller than the content.
        var item = MakeRealisticTable();
        var runs = RenderRuns(item, 24, out int measured);

        int distinctRows = runs.Select(r => r.Y).Distinct().Count();
        Assert.Equal(measured, distinctRows);
        Assert.True(runs.Any(r => r.Content.Contains('│')), "vertical separators must survive narrow widths");
    }

    [Fact]
    public void RenderSlice_RespectsWindow_DoesNotDrawAboveOrigin()
    {
        var item = MakeRealisticTable();
        int total = item.MeasureLineCount(80);

        var baseDl = new DL.DisplayListBuilder().Build();
        var b = new DL.DisplayListBuilder();
        // Render a 2-row window starting at data rows.
        item.RenderSlice(0, 100, 80, startLine: 3, maxLines: 2, baseDl, b);
        var ys = b.Build().Ops.OfType<DL.TextRun>()
            .Where(t => !string.IsNullOrEmpty(t.Content)).Select(t => t.Y).ToList();

        Assert.NotEmpty(ys);
        Assert.All(ys, y => Assert.InRange(y, 100, 101));
        Assert.True(total >= 5);
    }
}
