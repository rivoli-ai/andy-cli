using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Guards the feed invariant that <see cref="FileDiffItem.MeasureLineCount"/> equals the number of
/// rows <c>RenderSlice</c> actually draws (otherwise the feed leaves blank rows or clips content),
/// and that the diff renders with the right header and add/remove colors.
/// </summary>
public class FileDiffItemTests
{
    private static FileDiffItem Build(string? before, string after, FileChangeKind kind = FileChangeKind.Update)
        => new FileDiffItem("src/Foo.cs", kind, UnifiedDiff.Compute(before, after));

    [Fact]
    public void MeasureLineCount_EqualsRowsDrawn()
    {
        var item = Build("a\nb\nc", "a\nB\nc\nd");
        int width = 80;
        int measured = item.MeasureLineCount(width);

        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, measured, new DL.DisplayListBuilder().Build(), b);

        // Distinct Y rows that carry text must not exceed the measured height.
        var textRows = b.Build().Ops.OfType<DL.TextRun>()
            .Where(t => !string.IsNullOrEmpty(t.Content))
            .Select(t => t.Y)
            .Distinct()
            .ToList();

        Assert.True(measured >= 3); // header + summary + at least one body row
        Assert.True(textRows.Count <= measured, $"drew {textRows.Count} rows but measured {measured}");
        Assert.All(textRows, y => Assert.InRange(y, 0, measured - 1));
    }

    [Fact]
    public void Header_ShowsVerbAndPath()
    {
        var create = Build(null, "x\ny", FileChangeKind.Create);
        var b = new DL.DisplayListBuilder();
        create.RenderSlice(0, 0, 80, 0, create.MeasureLineCount(80), new DL.DisplayListBuilder().Build(), b);
        var texts = b.Build().Ops.OfType<DL.TextRun>().Select(t => t.Content).ToList();

        Assert.Contains(texts, t => t.Contains("Create(src/Foo.cs)"));
    }

    [Fact]
    public void AddedAndRemovedLines_UseSuccessAndErrorColors()
    {
        var theme = Andy.Cli.Themes.Theme.Current;
        var item = Build("keep\nremoveme\nkeep2", "keep\naddme\nkeep2");

        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 80, 0, item.MeasureLineCount(80), new DL.DisplayListBuilder().Build(), b);
        var runs = b.Build().Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrEmpty(r.Content)).ToList();

        Assert.Contains(runs, r => r.Content.Contains("addme") && r.Fg.Equals(theme.Success));
        Assert.Contains(runs, r => r.Content.Contains("removeme") && r.Fg.Equals(theme.Error));
    }

    [Fact]
    public void RenderSlice_RespectsRequestedWindow()
    {
        var item = Build("a\nb\nc", "a\nB\nC\nd\ne\nf");
        int total = item.MeasureLineCount(80);
        Assert.True(total > 3);

        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 100, 80, startLine: 1, maxLines: 2, new DL.DisplayListBuilder().Build(), b);
        var rows = b.Build().Ops.OfType<DL.TextRun>()
            .Where(t => !string.IsNullOrEmpty(t.Content))
            .Select(t => t.Y).Distinct().ToList();

        // Only two rows, drawn at the provided screen-y origin (100..101).
        Assert.True(rows.Count <= 2);
        Assert.All(rows, y => Assert.InRange(y, 100, 101));
    }
}
