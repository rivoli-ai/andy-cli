using System.Linq;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class StreamingMessageItemTests
{
    [Fact]
    public void EmptyItem_MeasuresAndRendersLoadingRow()
    {
        var item = new StreamingMessageItem();
        var builder = new DL.DisplayListBuilder();

        var measured = item.MeasureLineCount(40);
        item.RenderSlice(0, 0, 40, 0, measured, new DL.DisplayListBuilder().Build(), builder);

        Assert.Equal(1, measured);
        Assert.Contains(builder.Build().Ops.OfType<DL.TextRun>(), run => run.Content == "...");
    }

    [Fact]
    public void PlainMultilineContent_RendersWithinMeasuredHeight()
    {
        var item = new StreamingMessageItem();
        item.AppendContent("first line\nsecond line");
        const int width = 40;
        var measured = item.MeasureLineCount(width);
        var builder = new DL.DisplayListBuilder();

        item.RenderSlice(
            0, 0, width, 0, measured, new DL.DisplayListBuilder().Build(), builder);

        var rows = builder.Build().Ops.OfType<DL.TextRun>()
            .Where(run => !string.IsNullOrEmpty(run.Content))
            .Select(run => run.Y)
            .Distinct()
            .ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.InRange(row, 0, measured - 1));
    }
}
