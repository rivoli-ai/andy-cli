using System;
using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Tests for <see cref="FeedView"/> auto-scroll (bottom-follow) behavior:
///  - New content auto-scrolls to the bottom while the view is pinned to the bottom.
///  - When the user has scrolled up to read history, appended content does NOT
///    yank them to the bottom; their view is held steady.
///  - Auto-scroll resumes when the user scrolls back to (or near) the bottom, or
///    after they have been idle for a while after scrolling.
/// </summary>
public class FeedViewAutoScrollTests
{
    private const int Width = 80;
    private const int Height = 10;

    private static void Render(FeedView feed)
    {
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        feed.Render(new L.Rect(2, 3, Width, Height), baseDl, builder);
    }

    private static void AddLines(FeedView feed, int count, int startIndex = 0)
    {
        feed.AddMarkdown(string.Join("\n",
            Enumerable.Range(startIndex, count).Select(i => $"line {i}")));
    }

    [Fact]
    public void NewFeed_IsAutoScrollEnabled()
    {
        var feed = new FeedView();
        Assert.True(feed.IsAutoScrollEnabled);
    }

    [Fact]
    public void Append_WhenPinnedToBottom_StaysAtBottom()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);
        Assert.Equal(0, feed.ScrollOffset);
        Assert.True(feed.IsAutoScrollEnabled);

        // Appending more content while pinned keeps us at the bottom.
        AddLines(feed, 50, startIndex: 100);
        Render(feed);

        Assert.Equal(0, feed.ScrollOffset);
        Assert.True(feed.IsAutoScrollEnabled);
    }

    [Fact]
    public void ScrollUp_DisablesAutoScroll()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);

        feed.ScrollLines(20, Height); // scroll up well past the near-bottom band

        Assert.True(feed.ScrollOffset > FeedView.PinnedToBottomThresholdLines);
        Assert.False(feed.IsAutoScrollEnabled);
    }

    [Fact]
    public void Append_WhenScrolledUp_DoesNotYankToBottom_AndHoldsView()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);

        int offsetBefore = feed.ScrollLines(20, Height); // user reading history
        Render(feed);
        Assert.False(feed.IsAutoScrollEnabled);

        // New content arrives below the viewport.
        AddLines(feed, 30, startIndex: 100);
        Render(feed);

        // Not yanked to bottom.
        Assert.NotEqual(0, feed.ScrollOffset);
        Assert.False(feed.IsAutoScrollEnabled);
        // View held steady: offset grew by the number of appended lines so the same
        // content stays under the user's eyes.
        Assert.Equal(offsetBefore + 30, feed.ScrollOffset);
    }

    [Fact]
    public void ScrollBackToBottom_ReenablesAutoScroll_AndFollowsNewContent()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);

        feed.ScrollLines(20, Height); // scroll up
        Render(feed);
        Assert.False(feed.IsAutoScrollEnabled);

        feed.ScrollLines(-100_000, Height); // scroll back to the bottom
        Assert.Equal(0, feed.ScrollOffset);
        Assert.True(feed.IsAutoScrollEnabled);

        // Now new content follows the tail again.
        AddLines(feed, 25, startIndex: 100);
        Render(feed);
        Assert.Equal(0, feed.ScrollOffset);
    }

    [Fact]
    public void ScrollWithinNearBottomBand_KeepsAutoScrollEnabled()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);

        // Scroll up by exactly the pinned threshold: still counts as pinned.
        feed.ScrollLines(FeedView.PinnedToBottomThresholdLines, Height);
        Assert.True(feed.IsAutoScrollEnabled);

        AddLines(feed, 10, startIndex: 100);
        Render(feed);

        // Auto-scroll snapped us back to the bottom.
        Assert.Equal(0, feed.ScrollOffset);
    }

    [Fact]
    public void Append_AfterIdleResumeThreshold_ReturnsToBottom()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var feed = new FeedView();
        feed.SetClockForTesting(() => now);

        AddLines(feed, 100);
        Render(feed);

        feed.ScrollLines(20, Height); // user scrolls up at t0
        Render(feed);
        Assert.False(feed.IsAutoScrollEnabled);

        // Time passes beyond the idle-resume threshold without further scrolling.
        now = now + FeedView.AutoScrollIdleResumeThreshold + TimeSpan.FromSeconds(1);

        AddLines(feed, 10, startIndex: 100);
        Render(feed);

        // Idle long enough: auto-scroll resumed and snapped to the bottom.
        Assert.Equal(0, feed.ScrollOffset);
        Assert.True(feed.IsAutoScrollEnabled);
    }

    [Fact]
    public void Append_BeforeIdleResumeThreshold_StaysScrolledUp()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var feed = new FeedView();
        feed.SetClockForTesting(() => now);

        AddLines(feed, 100);
        Render(feed);

        feed.ScrollLines(20, Height); // user scrolls up at t0
        Render(feed);

        // Only a short time passes (below the idle threshold).
        now = now + TimeSpan.FromSeconds(1);

        AddLines(feed, 10, startIndex: 100);
        Render(feed);

        // Still reading history: not yanked to the bottom.
        Assert.NotEqual(0, feed.ScrollOffset);
        Assert.False(feed.IsAutoScrollEnabled);
    }

    [Fact]
    public void Clear_ResetsToAutoScrollAtBottom()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);
        feed.ScrollLines(20, Height);
        Render(feed);
        Assert.False(feed.IsAutoScrollEnabled);

        feed.Clear();

        Assert.Equal(0, feed.ScrollOffset);
        Assert.True(feed.IsAutoScrollEnabled);
    }
}
