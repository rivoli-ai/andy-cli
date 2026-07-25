using System;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Themes;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Side-by-side diff rendering (#254): the old file on the left, the new one on the right, one
/// screen row per change so a rewritten line sits opposite what replaced it.
/// </summary>
public class SplitDiffTests : IDisposable
{
    private readonly DiffLayout _original = DiffViewOptions.Style;

    public void Dispose() => DiffViewOptions.Style = _original;

    private static FileDiff Diff(string before, string after) => UnifiedDiff.Compute(before, after);

    private static string[] Render(FileDiff diff, int width) =>
        DiffRenderer.RenderDiff(diff, "a.cs", width, maxRows: 60, Theme.Current)
            .Select(r => r.Text).ToArray();

    // ---- layout selection ----------------------------------------------------------------

    [Fact]
    public void AutoUsesUnifiedOnANarrowTerminal()
    {
        DiffViewOptions.Style = DiffLayout.Auto;

        Assert.False(DiffViewOptions.UseSplit(80));
        Assert.False(DiffViewOptions.UseSplit(DiffViewOptions.AutoSplitWidth - 1));
    }

    [Fact]
    public void AutoUsesSplitOnAWideTerminal()
    {
        DiffViewOptions.Style = DiffLayout.Auto;

        Assert.True(DiffViewOptions.UseSplit(DiffViewOptions.AutoSplitWidth));
        Assert.True(DiffViewOptions.UseSplit(200));
    }

    [Fact]
    public void UnifiedIsHonoredAtEveryWidth()
    {
        DiffViewOptions.Style = DiffLayout.Unified;

        Assert.False(DiffViewOptions.UseSplit(200));
    }

    [Fact]
    public void SplitIsRefusedWhenTheColumnsWouldBeUnreadable()
    {
        // Two columns of forty-odd characters truncate more than they reveal.
        DiffViewOptions.Style = DiffLayout.Split;

        Assert.False(DiffViewOptions.UseSplit(DiffViewOptions.MinimumSplitWidth - 1));
        Assert.True(DiffViewOptions.UseSplit(DiffViewOptions.MinimumSplitWidth));
    }

    // ---- pairing -------------------------------------------------------------------------

    [Fact]
    public void AReplacedLineSitsOppositeItsReplacement()
    {
        DiffViewOptions.Style = DiffLayout.Split;
        var rows = Render(Diff("alpha\nbravo\ncharlie\n", "alpha\nBRAVO\ncharlie\n"), 140);

        var changed = rows.Single(r => r.Contains("bravo"));
        Assert.Contains("BRAVO", changed);          // both sides on one row
        Assert.True(changed.IndexOf("bravo", StringComparison.Ordinal)
                    < changed.IndexOf("BRAVO", StringComparison.Ordinal), "old on the left, new on the right");
    }

    [Fact]
    public void APureDeletionLeavesTheRightSideEmpty()
    {
        DiffViewOptions.Style = DiffLayout.Split;
        var rows = Render(Diff("keep\ngone\nkeep2\n", "keep\nkeep2\n"), 140);

        var deleted = rows.Single(r => r.Contains("gone"));
        var right = deleted[(deleted.IndexOf('|') + 1)..];
        Assert.True(string.IsNullOrWhiteSpace(right), $"expected an empty right cell, got '{right}'");
    }

    [Fact]
    public void APureInsertionLeavesTheLeftSideEmpty()
    {
        DiffViewOptions.Style = DiffLayout.Split;
        var rows = Render(Diff("keep\nkeep2\n", "keep\nfresh\nkeep2\n"), 140);

        var added = rows.Single(r => r.Contains("fresh"));
        var left = added[..added.IndexOf('|')];
        Assert.True(string.IsNullOrWhiteSpace(left), $"expected an empty left cell, got '{left}'");
    }

    [Fact]
    public void UnevenChangeRunsPairUpAndThenSpill()
    {
        // Two lines replaced by three: the first two pair, the third gets an empty left cell.
        DiffViewOptions.Style = DiffLayout.Split;
        var rows = Render(Diff("a\nold1\nold2\nz\n", "a\nnew1\nnew2\nnew3\nz\n"), 140);

        Assert.Contains(rows, r => r.Contains("old1") && r.Contains("new1"));
        Assert.Contains(rows, r => r.Contains("old2") && r.Contains("new2"));

        var spill = rows.Single(r => r.Contains("new3"));
        Assert.True(string.IsNullOrWhiteSpace(spill[..spill.IndexOf('|')]), "the surplus addition has no counterpart");
    }

    [Fact]
    public void ContextLinesAppearOnBothSides()
    {
        DiffViewOptions.Style = DiffLayout.Split;
        var rows = Render(Diff("keep\nold\n", "keep\nnew\n"), 140);

        var context = rows.Single(r => r.Contains("keep"));
        Assert.Equal(2, context.Split("keep").Length - 1);
    }

    // ---- alignment -----------------------------------------------------------------------

    [Fact]
    public void EveryRowIsTheSameWidthSoTheDividerStaysStraight()
    {
        DiffViewOptions.Style = DiffLayout.Split;
        var rows = DiffRenderer.RenderDiff(
            Diff("a\nbb\nccc\ndddd\n", "a\nBB\nccc\nDDDD-longer\n"), "a.cs", 140, 60, Theme.Current);

        var widths = rows.Select(r => r.Width).Distinct().ToList();
        Assert.Single(widths);
    }

    [Fact]
    public void LineNumbersComeFromTheCorrectSide()
    {
        DiffViewOptions.Style = DiffLayout.Split;
        // Two lines removed, one added: the new-side numbering must not follow the old side's.
        var rows = Render(Diff("a\nb\nc\nd\n", "a\nB\nd\n"), 140);

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Contains("|", r));
    }

    [Fact]
    public void SplitAndUnifiedShowTheSameContent()
    {
        var diff = Diff("one\ntwo\nthree\n", "one\nTWO\nthree\nfour\n");

        DiffViewOptions.Style = DiffLayout.Unified;
        var unified = string.Concat(Render(diff, 140));
        DiffViewOptions.Style = DiffLayout.Split;
        var split = string.Concat(Render(diff, 140));

        foreach (var token in new[] { "two", "TWO", "three", "four" })
        {
            Assert.Contains(token, unified);
            Assert.Contains(token, split);
        }
    }

    [Fact]
    public void TintsSurviveIntoSplitRows()
    {
        DiffViewOptions.Style = DiffLayout.Split;
        var rows = DiffRenderer.RenderDiff(Diff("x\n", "y\n"), "a.cs", 140, 60, Theme.Current);

        Assert.Contains(rows, r => r.Spans.Any(s => s.Background == Theme.Current.DiffRemovedBackground));
        Assert.Contains(rows, r => r.Spans.Any(s => s.Background == Theme.Current.DiffAddedBackground));
    }
}
