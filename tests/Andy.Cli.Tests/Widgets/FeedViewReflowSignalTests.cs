using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Tests for the reflow signals (<see cref="FeedView.ItemCount"/> and
/// <see cref="FeedView.RenderedLineCount"/>) that Program.cs combines into a
/// reflow signature to force a full clear+repaint, wiping stale margin residue
/// left behind by transparent backgrounds. See commit "Clear stale margin
/// residue on content reflow".
/// </summary>
public class FeedViewReflowSignalTests
{
    private static (DL.DisplayList baseDl, DL.DisplayListBuilder builder) NewRenderContext()
    {
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        return (baseDl, builder);
    }

    private static void Render(FeedView feed, int width = 80, int height = 24)
    {
        var (baseDl, builder) = NewRenderContext();
        feed.Render(new L.Rect(2, 3, width, height), baseDl, builder);
    }

    [Fact]
    public void ItemCount_StartsAtZero()
    {
        var feed = new FeedView();
        Assert.Equal(0, feed.ItemCount);
    }

    [Fact]
    public void ItemCount_TracksAddedItems()
    {
        var feed = new FeedView();

        feed.AddMarkdown("first");
        Assert.Equal(1, feed.ItemCount);

        feed.AddMarkdown("second");
        feed.AddMarkdown("third");
        Assert.Equal(3, feed.ItemCount);
    }

    [Fact]
    public void ItemCount_ResetsToZeroAfterClear()
    {
        var feed = new FeedView();
        feed.AddMarkdown("a");
        feed.AddMarkdown("b");
        Assert.Equal(2, feed.ItemCount);

        feed.Clear();
        Assert.Equal(0, feed.ItemCount);
    }

    [Fact]
    public void RenderedLineCount_IsZeroBeforeFirstRender()
    {
        var feed = new FeedView();
        feed.AddMarkdown("content that has not been rendered yet");

        // RenderedLineCount reflects the *last render*, so it stays zero until Render runs.
        Assert.Equal(0, feed.RenderedLineCount);
    }

    [Fact]
    public void RenderedLineCount_IsPositiveAfterRenderingContent()
    {
        var feed = new FeedView();
        feed.AddMarkdown("line one\nline two\nline three");

        Render(feed);

        Assert.True(feed.RenderedLineCount > 0,
            $"Expected rendered lines for non-empty content, got {feed.RenderedLineCount}");
    }

    [Fact]
    public void RenderedLineCount_GrowsWhenMoreContentIsAdded()
    {
        var feed = new FeedView();
        feed.AddMarkdown("single line");
        Render(feed);
        int afterFirst = feed.RenderedLineCount;

        feed.AddMarkdown("another\nblock\nof\nseveral\nlines");
        Render(feed);
        int afterSecond = feed.RenderedLineCount;

        Assert.True(afterSecond > afterFirst,
            $"Expected line count to grow on reflow: {afterFirst} -> {afterSecond}");
    }

    [Fact]
    public void RenderedLineCount_ReflectsMultiLineContent()
    {
        // A markdown item maps one input line to one rendered row, so RenderedLineCount
        // grows with the number of content lines — the reflow signal Program.cs relies on.
        var single = new FeedView();
        single.AddMarkdown("one");
        Render(single);
        int singleLines = single.RenderedLineCount;

        var multi = new FeedView();
        multi.AddMarkdown("one\ntwo\nthree\nfour");
        Render(multi);
        int multiLines = multi.RenderedLineCount;

        Assert.True(multiLines > singleLines,
            $"Expected more rows for multi-line content: single={singleLines}, multi={multiLines}");
    }

    [Fact]
    public void ReflowSignature_ChangesWhenContentChanges()
    {
        // Mirrors the signature Program.cs builds: HashCode.Combine(ItemCount, RenderedLineCount, scrollMode).
        // A different signature is what triggers the full clear+repaint that wipes margin residue.
        var feed = new FeedView();
        Render(feed);
        int empty = System.HashCode.Combine(feed.ItemCount, feed.RenderedLineCount, 0);

        feed.AddMarkdown("now there is content");
        Render(feed);
        int withContent = System.HashCode.Combine(feed.ItemCount, feed.RenderedLineCount, 0);

        Assert.NotEqual(empty, withContent);
    }
}
