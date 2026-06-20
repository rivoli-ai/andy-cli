using System.Linq;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class UserBubbleItemTests
{
    private static string Render(UserBubbleItem item, int width)
    {
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, item.MeasureLineCount(width), new DL.DisplayListBuilder().Build(), b);
        return string.Concat(b.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content));
    }

    [Fact]
    public void LongSingleLine_WrapsAndKeepsAllContent()
    {
        // The reported case: a long message that overflows the bubble width must wrap, not truncate.
        var msg = "Load /tmp/andy-data-demo/sales.csv as a dataset called \"sales\", then show its schema and the first 5 rows.";
        var item = new UserBubbleItem(msg, 1);

        int width = 48;
        Assert.True(item.MeasureLineCount(width) > 3, "a long message should wrap to multiple rows");

        var text = Render(item, width);
        Assert.Contains("You (#1)", text);
        Assert.Contains("schema", text);
        Assert.Contains("5 rows.", text);   // the tail would be lost if it truncated at the first row
    }

    [Fact]
    public void MultiLineMessage_PreservesEveryLine()
    {
        var item = new UserBubbleItem("first line here\nsecond line here\nthird line here", 2);
        var text = Render(item, 60);
        Assert.Contains("first line here", text);
        Assert.Contains("second line here", text);
        Assert.Contains("third line here", text);
    }

    [Fact]
    public void HardBreaksTokenLongerThanWidth_LosesNoCharacters()
    {
        // A no-space token (path/URL) longer than the bubble must hard-break across rows without
        // dropping any character (it may split mid-token, which is fine).
        var longPath = "/very/long/path/segment/that/exceeds/the/bubble/width/file.csv";
        var item = new UserBubbleItem(longPath, 0);
        int width = 24;

        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, item.MeasureLineCount(width), new DL.DisplayListBuilder().Build(), b);
        // Body text uses the message text color (220,220,220); label is a different color and
        // borders another. Reconstruct the message from the body runs in reading order.
        var bodyColor = new DL.Rgb24(220, 220, 220);
        var reconstructed = string.Concat(b.Build().Ops.OfType<DL.TextRun>()
            .Where(r => r.Fg.HasValue && r.Fg.Value.Equals(bodyColor))
            .OrderBy(r => r.Y).ThenBy(r => r.X)
            .Select(r => r.Content));
        Assert.Equal(longPath, reconstructed);
    }

    [Fact]
    public void Label_IsInline_OnTheSameRowAsTheMessageStart()
    {
        var item = new UserBubbleItem("hello world", 3);
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 60, 0, item.MeasureLineCount(60), new DL.DisplayListBuilder().Build(), b);
        var runs = b.Build().Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrEmpty(r.Content)).ToList();

        var label = runs.First(r => r.Content.Contains("You (#3)"));
        var firstText = runs.First(r => r.Content.Contains("hello"));
        Assert.Equal(label.Y, firstText.Y);   // message starts on the SAME row as the label
        Assert.True(firstText.X > label.X);    // immediately after the label
    }

    [Fact]
    public void MeasureMatchesRenderedRowCount()
    {
        var item = new UserBubbleItem("alpha beta gamma delta epsilon zeta eta theta iota kappa", 3);
        int width = 30;
        int measured = item.MeasureLineCount(width);

        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, measured, new DL.DisplayListBuilder().Build(), b);
        var maxRow = b.Build().Ops.OfType<DL.TextRun>().Select(r => r.Y).DefaultIfEmpty(-1).Max();
        Assert.Equal(measured - 1, maxRow); // last drawn row is the bottom border at measured-1
    }
}
