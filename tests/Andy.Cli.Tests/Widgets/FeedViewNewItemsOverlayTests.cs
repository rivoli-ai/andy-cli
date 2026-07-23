using System;
using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Tests for the FeedView "N new messages" overlay (issue #226): when output
/// arrives while the user is scrolled up in the transcript, an indicator on the
/// last visible line reports how many conversation items are pending below, and
/// it disappears as soon as the user returns to the bottom.
/// </summary>
public class FeedViewNewItemsOverlayTests
{
    private const int Width = 80;
    private const int Height = 10;

    private static DL.DisplayList Render(FeedView feed)
    {
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        feed.Render(new L.Rect(2, 3, Width, Height), baseDl, builder);
        return builder.Build();
    }

    private static void AddLines(FeedView feed, int count, int startIndex = 0)
    {
        feed.AddMarkdown(string.Join("\n",
            Enumerable.Range(startIndex, count).Select(i => $"line {i}")));
    }

    [Fact]
    public void AtBottom_AppendedItems_DoNotCount()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);

        AddLines(feed, 10, startIndex: 100);
        Render(feed);

        Assert.Equal(0, feed.NewItemsBelowCount);
        Assert.False(feed.IsNewItemsOverlayVisible);
    }

    [Fact]
    public void ScrolledUp_AppendedItems_IncrementCounterAndShowOverlay()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);

        feed.ScrollLines(20, Height); // user reading history
        Render(feed);
        Assert.Equal(0, feed.NewItemsBelowCount);
        Assert.False(feed.IsNewItemsOverlayVisible);

        AddLines(feed, 5, startIndex: 100);  // one item
        AddLines(feed, 5, startIndex: 105);  // second item
        AddLines(feed, 5, startIndex: 110);  // third item
        Render(feed);

        Assert.Equal(3, feed.NewItemsBelowCount);
        Assert.True(feed.IsNewItemsOverlayVisible);
    }

    [Fact]
    public void ScrollBackToBottom_ClearsCounterAndHidesOverlay()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);

        feed.ScrollLines(20, Height);
        Render(feed);
        AddLines(feed, 5, startIndex: 100);
        Render(feed);
        Assert.True(feed.IsNewItemsOverlayVisible);

        feed.ScrollLines(-100_000, Height); // back to the bottom

        Assert.Equal(0, feed.NewItemsBelowCount);
        Assert.False(feed.IsNewItemsOverlayVisible);
    }

    [Fact]
    public void ScrollBackIntoNearBottomBand_ClearsCounter()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);

        feed.ScrollLines(20, Height);
        Render(feed);
        AddLines(feed, 5, startIndex: 100);
        Render(feed);
        Assert.True(feed.NewItemsBelowCount > 0);

        // Scroll down to within the pinned-to-bottom band (auto-scroll resumes there).
        int offset = feed.ScrollOffset;
        feed.ScrollLines(-(offset - FeedView.PinnedToBottomThresholdLines), Height);

        Assert.True(feed.IsAutoScrollEnabled);
        Assert.Equal(0, feed.NewItemsBelowCount);
        Assert.False(feed.IsNewItemsOverlayVisible);
    }

    [Fact]
    public void SnapToBottom_ClearsCounter()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);
        feed.ScrollLines(20, Height);
        Render(feed);
        AddLines(feed, 5, startIndex: 100);
        Render(feed);
        Assert.True(feed.NewItemsBelowCount > 0);

        feed.SnapToBottom(); // e.g. the user starts typing

        Assert.Equal(0, feed.NewItemsBelowCount);
        Assert.False(feed.IsNewItemsOverlayVisible);
    }

    [Fact]
    public void Clear_ResetsCounter()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);
        feed.ScrollLines(20, Height);
        Render(feed);
        AddLines(feed, 5, startIndex: 100);
        Render(feed);
        Assert.True(feed.NewItemsBelowCount > 0);

        feed.Clear();

        Assert.Equal(0, feed.NewItemsBelowCount);
        Assert.False(feed.IsNewItemsOverlayVisible);
    }

    [Fact]
    public void IdleResumeAppend_SnapsToBottom_AndClearsCounter()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var feed = new FeedView();
        feed.SetClockForTesting(() => now);

        AddLines(feed, 100);
        Render(feed);
        feed.ScrollLines(20, Height);
        Render(feed);

        AddLines(feed, 5, startIndex: 100); // arrives while reading: counted
        Render(feed);
        Assert.Equal(1, feed.NewItemsBelowCount);

        // Idle long enough for auto-scroll to resume; the next append snaps to
        // the bottom, so the pending content becomes visible.
        now = now + FeedView.AutoScrollIdleResumeThreshold + TimeSpan.FromSeconds(1);
        AddLines(feed, 5, startIndex: 105);
        Render(feed);

        Assert.Equal(0, feed.ScrollOffset);
        Assert.Equal(0, feed.NewItemsBelowCount);
        Assert.False(feed.IsNewItemsOverlayVisible);
    }

    [Fact]
    public void Render_WhenOverlayVisible_DrawsLabelOnLastVisibleLine()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);
        feed.ScrollLines(20, Height);
        Render(feed);
        AddLines(feed, 5, startIndex: 100);
        AddLines(feed, 5, startIndex: 105);
        var dl = Render(feed);

        var run = dl.Ops.OfType<DL.TextRun>()
            .SingleOrDefault(r => r.Content.Contains("2 new messages"));
        // Rect passed to Render is (x:2, y:3, h:10): last visible line is y 12.
        Assert.Equal(3 + Height - 1, run!.Y);
        // Plain ASCII only.
        Assert.All(run.Content, c => Assert.InRange((int)c, 0x20, 0x7E));
    }

    [Fact]
    public void Render_AtBottom_DrawsNoOverlay()
    {
        var feed = new FeedView();
        AddLines(feed, 100);
        Render(feed);
        AddLines(feed, 5, startIndex: 100);
        var dl = Render(feed);

        Assert.DoesNotContain(dl.Ops.OfType<DL.TextRun>(),
            r => r.Content.Contains("new message"));
    }

    [Theory]
    [InlineData(1, "1 new message")]
    [InlineData(2, "2 new messages")]
    [InlineData(17, "17 new messages")]
    public void FormatNewItemsOverlay_UsesSingularAndPlural(int count, string expected)
    {
        Assert.Equal(expected, FeedView.FormatNewItemsOverlay(count));
    }
}
