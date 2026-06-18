using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Tests for <see cref="FeedView.ScrollLines"/> bounds. The scroll offset must
/// clamp to <c>total - viewportHeight</c> (the highest offset that still shows
/// content), not to <c>total</c>. Clamping to <c>total</c> left a dead-zone
/// above the top where scrolling back down appeared to do nothing — part of why
/// PageUp/PageDown and the wheel felt unresponsive.
/// </summary>
public class FeedViewScrollClampTests
{
    private const int Width = 80;
    private const int Height = 10;

    private static void Render(FeedView feed)
    {
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        feed.Render(new L.Rect(2, 3, Width, Height), baseDl, builder);
    }

    private static FeedView FeedWithLines(int lineCount)
    {
        var feed = new FeedView();
        feed.AddMarkdown(string.Join("\n", Enumerable.Range(0, lineCount).Select(i => $"line {i}")));
        Render(feed); // populates the line cache and remembers the viewport height
        return feed;
    }

    [Fact]
    public void ScrollUp_ClampsToTotalMinusViewportHeight()
    {
        var feed = FeedWithLines(100);
        int total = feed.RenderedLineCount;
        Assert.True(total > Height, "test needs more content than the viewport");

        // Scroll far past the top; offset must stop at total - viewportHeight.
        int offset = feed.ScrollLines(100_000, Height);

        Assert.Equal(total - Height, offset);
    }

    [Fact]
    public void ScrollDownAfterMax_MovesImmediately_NoDeadZone()
    {
        var feed = FeedWithLines(100);
        int max = feed.ScrollLines(100_000, Height); // pin to the top

        // One page down from the top must move by the full delta (no dead-zone
        // of excess offset to unwind first).
        int afterPageDown = feed.ScrollLines(-Height, Height);

        Assert.Equal(max - Height, afterPageDown);
    }

    [Fact]
    public void ScrollDownToBottom_ClampsToZero()
    {
        var feed = FeedWithLines(100);
        feed.ScrollLines(100_000, Height); // top
        int offset = feed.ScrollLines(-100_000, Height); // bottom

        Assert.Equal(0, offset);
    }

    [Fact]
    public void Scrolling_WhenContentFitsViewport_StaysAtZero()
    {
        var feed = FeedWithLines(3); // fewer lines than the viewport height
        int offset = feed.ScrollLines(100_000, Height);

        Assert.Equal(0, offset);
    }

    // Regression for rivoli-ai/andy-cli#122: PageUp/PageDown must move exactly one page (not 2x the
    // viewport), so no page is skipped. PageUp/PageDown use the int.MaxValue/MinValue "one page"
    // sentinels; one page == pageSize lines, consecutive pages are contiguous, and up-then-down
    // round-trips to the same offset.
    [Fact]
    public void PageSentinel_MovesExactlyOnePage_AndDoesNotSkip()
    {
        var feed = FeedWithLines(100);
        const int page = Height - 5; // matches the CLI's page step (viewport.Height - 5)

        int afterFirst = feed.ScrollLines(int.MaxValue, page);   // PageUp once
        Assert.Equal(page, afterFirst);                          // exactly one page, not 2x

        int afterSecond = feed.ScrollLines(int.MaxValue, page);  // PageUp again
        Assert.Equal(2 * page, afterSecond);                     // contiguous — no page skipped

        int afterDown = feed.ScrollLines(int.MinValue, page);    // PageDown once
        Assert.Equal(page, afterDown);                           // returns to the previous page
    }
}
