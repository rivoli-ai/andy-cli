using System.Linq;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class UnifiedDiffTests
{
    [Fact]
    public void IdenticalText_IsEmpty()
    {
        var diff = UnifiedDiff.Compute("a\nb\nc", "a\nb\nc");
        Assert.True(diff.IsEmpty);
        Assert.Equal(0, diff.AddedCount);
        Assert.Equal(0, diff.RemovedCount);
        Assert.Empty(diff.Lines);
    }

    [Fact]
    public void CreateFromEmpty_AllLinesAdded_WithSequentialNewNumbers()
    {
        var diff = UnifiedDiff.Compute(null, "one\ntwo\nthree");

        Assert.Equal(3, diff.AddedCount);
        Assert.Equal(0, diff.RemovedCount);
        Assert.All(diff.Lines, l => Assert.Equal(DiffLineKind.Added, l.Kind));
        Assert.Equal(new[] { 1, 2, 3 }, diff.Lines.Select(l => l.NewLineNumber!.Value).ToArray());
        Assert.All(diff.Lines, l => Assert.Null(l.OldLineNumber));
    }

    [Fact]
    public void AppendedLines_ShowAsAdded_WithContext_NoRemovals()
    {
        var before = "a\nb\nc";
        var after = "a\nb\nc\nd\ne";
        var diff = UnifiedDiff.Compute(before, after);

        Assert.Equal(2, diff.AddedCount);
        Assert.Equal(0, diff.RemovedCount);
        var added = diff.Lines.Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text).ToArray();
        Assert.Equal(new[] { "d", "e" }, added);
    }

    [Fact]
    public void RemovedLines_ShowAsRemoved()
    {
        var diff = UnifiedDiff.Compute("a\nb\nc\nd", "a\nd");
        Assert.Equal(0, diff.AddedCount);
        Assert.Equal(2, diff.RemovedCount);
        var removed = diff.Lines.Where(l => l.Kind == DiffLineKind.Removed).Select(l => l.Text).ToArray();
        Assert.Equal(new[] { "b", "c" }, removed);
    }

    [Fact]
    public void ModifiedLine_IsRemovedThenAdded_AtCorrectNumbers()
    {
        var before = "line1\nOLD\nline3";
        var after = "line1\nNEW\nline3";
        var diff = UnifiedDiff.Compute(before, after);

        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.RemovedCount);

        var removed = diff.Lines.Single(l => l.Kind == DiffLineKind.Removed);
        Assert.Equal("OLD", removed.Text);
        Assert.Equal(2, removed.OldLineNumber);
        Assert.Null(removed.NewLineNumber);

        var added = diff.Lines.Single(l => l.Kind == DiffLineKind.Added);
        Assert.Equal("NEW", added.Text);
        Assert.Equal(2, added.NewLineNumber);
        Assert.Null(added.OldLineNumber);

        // Surrounding identical lines are kept as context.
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Context && l.Text == "line1");
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Context && l.Text == "line3");
    }

    [Fact]
    public void TrailingNewline_DoesNotCreateSpuriousEmptyLine()
    {
        // Both have trailing newlines; only "c" is added. A naive split would add a phantom "" line.
        var diff = UnifiedDiff.Compute("a\nb\n", "a\nb\nc\n");
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(0, diff.RemovedCount);
        Assert.Equal("c", diff.Lines.Single(l => l.Kind == DiffLineKind.Added).Text);
    }

    [Fact]
    public void DistantUnchangedRegions_AreCollapsedIntoGaps()
    {
        // 21 identical lines with a single change in the middle: far context collapses to gaps.
        var before = string.Join("\n", Enumerable.Range(1, 21).Select(i => $"line{i}"));
        var afterLines = Enumerable.Range(1, 21).Select(i => i == 11 ? "CHANGED" : $"line{i}");
        var after = string.Join("\n", afterLines);

        var diff = UnifiedDiff.Compute(before, after, contextLines: 2);

        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.RemovedCount);
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Gap);
        // line1 is far from the change (line 11) and must be elided, not shown as context.
        Assert.DoesNotContain(diff.Lines, l => l.Kind == DiffLineKind.Context && l.Text == "line1");
        // Immediate neighbours are kept.
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Context && l.Text == "line10");
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Context && l.Text == "line12");
    }

    [Fact]
    public void VeryLargeInput_DegradesToWholeFileReplace()
    {
        // Above the LCS cap (4000 lines) the algorithm degrades to all-removed + all-added.
        var before = string.Join("\n", Enumerable.Range(1, 4100).Select(i => $"a{i}"));
        var after = string.Join("\n", Enumerable.Range(1, 4100).Select(i => $"b{i}"));

        var diff = UnifiedDiff.Compute(before, after);

        Assert.Equal(4100, diff.AddedCount);
        Assert.Equal(4100, diff.RemovedCount);
        // Whole-file replace lists all removals first.
        Assert.Equal(DiffLineKind.Removed, diff.Lines.First(l => l.Kind != DiffLineKind.Gap).Kind);
    }
}
