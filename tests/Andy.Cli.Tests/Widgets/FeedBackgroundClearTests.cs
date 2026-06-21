using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// The feed must paint its full viewport with a background rect every frame, so cells that the
/// items no longer cover are cleared. Without it, content that shrinks or reflows (e.g. the
/// variable-height file-diff items) leaves phantom characters and stale, non-theme-colored
/// whitespace from a taller previous frame. A null (transparent) theme background still emits the
/// rect (clearing to the terminal default), so transparency is preserved.
/// </summary>
public class FeedBackgroundClearTests
{
    [Theory]
    [InlineData(50, 20)]
    [InlineData(96, 40)]
    public void Render_EmitsFullViewportBackgroundClear(int w, int h)
    {
        var feed = new FeedView();
        feed.AddMarkdownRich("hello");

        var b = new DL.DisplayListBuilder();
        feed.Render(new L.Rect(0, 0, w, h), new DL.DisplayListBuilder().Build(), b);

        var rects = b.Build().Ops.OfType<DL.Rect>().ToList();
        Assert.Contains(rects, r => r.X == 0 && r.Y == 0 && r.Width == w && r.Height == h);
    }
}
