using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Widgets;

public class ThinkingLayoutTests
{
    [Fact]
    public void HiddenContentReappearsWithConsistentSliceCount()
    {
        var old = ThinkingView.Visible;
        try
        {
            ThinkingView.Visible = true;
            var item = new ThinkingBlockItem();
            item.AppendContent("First line\n" + new string('x', 90));
            var count = item.MeasureLineCount(30);
            var builder = new DL.DisplayListBuilder();
            item.RenderSlice(0, 0, 30, 0, count, builder.Build(), builder);
            Assert.Equal(count, builder.Build().Ops.OfType<DL.TextRun>().Count());
            ThinkingView.Visible = false;
            Assert.Equal(0, item.MeasureLineCount(30));
            ThinkingView.Visible = true;
            Assert.Equal(count, item.MeasureLineCount(30));
            Assert.Contains("First line", item.GetContent());
        }
        finally { ThinkingView.Visible = old; }
    }
}
