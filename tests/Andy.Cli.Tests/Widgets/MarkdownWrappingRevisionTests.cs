using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Widgets;

public class MarkdownWrappingRevisionTests
{
    [Theory]
    [InlineData("alpha beta gamma", "alpha beta", "gamma")]
    [InlineData("**alpha beta** gamma", "alpha beta", "gamma")]
    [InlineData("<a href=\"https://example.test\">alpha beta gamma</a>", "alpha beta", "gamma")]
    [InlineData("abcdefghijk", "abcdefghij", "k")]
    public void WrapsAtWordsAfterParsingInlineStyles(string text, string first, string second)
    {
        var item = new MarkdownRendererItem(text);
        var builder = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 10, 0, item.MeasureLineCount(10), new DL.DisplayListBuilder().Build(), builder);
        var runs = builder.Build().Ops.OfType<DL.TextRun>().ToArray();
        Assert.Equal(new[] { first, second }, runs.GroupBy(r => r.Y).OrderBy(g => g.Key)
            .Select(g => string.Concat(g.OrderBy(r => r.X).Select(r => r.Content))));
        if (text.StartsWith("**"))
        {
            Assert.All(runs.Where(r => r.Y == 0), r => Assert.True(r.Attrs.HasFlag(DL.CellAttrFlags.Bold)));
            Assert.All(runs.Where(r => r.Y == 1), r => Assert.False(r.Attrs.HasFlag(DL.CellAttrFlags.Bold)));
        }
    }

    [Fact]
    public void ListContinuationKeepsIndentAndResizeRebuildsLayout()
    {
        var item = new MarkdownRendererItem("- alpha beta gamma delta");
        int narrow = item.MeasureLineCount(10);
        var builder = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 10, 0, narrow, new DL.DisplayListBuilder().Build(), builder);
        var continuations = builder.Build().Ops.OfType<DL.TextRun>().Where(r => r.Y > 0).GroupBy(r => r.Y);
        Assert.NotEmpty(continuations);
        Assert.All(continuations, row => Assert.Equal(2, row.Min(r => r.X)));
        Assert.True(item.MeasureLineCount(80) < narrow);
        Assert.Equal(narrow, item.MeasureLineCount(10));
    }

    [Fact]
    public void LongResponseRetainsItsFinalDisplayRow()
    {
        var item = new MarkdownRendererItem(string.Join("\n", Enumerable.Repeat("a", 9000)) + "\nEND");
        var count = item.MeasureLineCount(20);
        var builder = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 20, count - 1, 1, new DL.DisplayListBuilder().Build(), builder);
        var lastRow = string.Concat(builder.Build().Ops.OfType<DL.TextRun>().Where(r => r.Y == 0).OrderBy(r => r.X).Select(r => r.Content));
        Assert.Equal("END", lastRow);
    }

    [Theory]
    [InlineData("alpha\nbeta", 20)]
    [InlineData("a long paragraph with multiple words that wrap across several lines", 9)]
    [InlineData("# Heading\n\n```\ncode\n```\n\nlast line", 20)]
    public void EverySliceMatchesTheSameFullLayout(string text, int width)
    {
        var item = new MarkdownRendererItem(text);
        var baseDl = new DL.DisplayListBuilder().Build();
        var full = new DL.DisplayListBuilder();
        var count = item.MeasureLineCount(width);
        item.RenderSlice(0, 0, width, 0, count, baseDl, full);
        var all = full.Build().Ops.OfType<DL.TextRun>().ToArray();
        for (int row = 0; row < count; row++)
        {
            var slice = new DL.DisplayListBuilder();
            item.RenderSlice(0, 0, width, row, 1, baseDl, slice);
            Assert.Equal(all.Where(r => r.Y == row).Select(r => (r.X, r.Content, r.Attrs)),
                slice.Build().Ops.OfType<DL.TextRun>().Where(r => r.Y == 0).Select(r => (r.X, r.Content, r.Attrs)));
        }
        Assert.Contains(all, r => r.Y == count - 1 && r.Content.Length > 0);
    }
}
