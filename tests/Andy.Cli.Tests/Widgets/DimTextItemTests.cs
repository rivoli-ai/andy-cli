using System;
using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Session/status metadata lines (e.g. "[session] &lt;id&gt; (resume after exit: ...)") are dimmed
/// via <see cref="FeedView.AddDimText"/>, which applies the theme's dim color through the typed
/// TextRun.Fg field. They must NOT be dimmed by embedding raw ANSI escapes (ConsoleColors.Dim)
/// into markdown: the display-list contract treats TextRun.Content as untrusted plain text and
/// rewrites terminal control characters to visible placeholders, so ESC[90m/ESC[0m showed up
/// literally in the feed. These tests pin the no-control-characters behavior and the typed color.
/// </summary>
public class DimTextItemTests
{
    private static DL.DisplayList RenderFeed(FeedView feed, int w, int h)
    {
        var b = new DL.DisplayListBuilder();
        feed.Render(new L.Rect(0, 0, w, h), new DL.DisplayListBuilder().Build(), b);
        return b.Build();
    }

    [Fact]
    public void DimText_ContentHasNoAnsiEscapes()
    {
        var feed = new FeedView();
        feed.AddDimText("[session] 20260724-000823-31e3 (resume after exit: andy-cli --resume 20260724-000823-31e3)");

        var dl = RenderFeed(feed, 100, 10);

        var runs = dl.Ops.OfType<DL.TextRun>().Where(t => !string.IsNullOrEmpty(t.Content)).ToList();
        Assert.NotEmpty(runs);
        Assert.Contains(runs, t => t.Content.Contains("[session] 20260724-000823-31e3"));
        // The bug: content arriving at the renderer wrapped in ESC[90m ... ESC[0m rendered the
        // control characters literally. No emitted run may carry an ESC character.
        Assert.DoesNotContain(runs, t => t.Content.Contains('\u001b'));
        Assert.DoesNotContain(runs, t => t.Content.Contains("[90m") || t.Content.Contains("[0m"));
    }

    [Fact]
    public void DimText_UsesThemeDimForeground()
    {
        var feed = new FeedView();
        feed.AddDimText("[session] some-id");

        var dl = RenderFeed(feed, 80, 10);

        var expected = Andy.Cli.Themes.Theme.Current.TextDim;
        var run = dl.Ops.OfType<DL.TextRun>()
            .First(t => t.Content != null && t.Content.Contains("[session] some-id"));
        Assert.True(run.Fg.HasValue, "Dim text must set the typed Fg color rather than embedding escapes.");
        Assert.Equal(expected.R, run.Fg!.Value.R);
        Assert.Equal(expected.G, run.Fg!.Value.G);
        Assert.Equal(expected.B, run.Fg!.Value.B);
    }

    [Theory]
    [InlineData(200, 40)] // fits on one line at width 200? no - capped by feed width 40 -> wraps
    [InlineData(60, 30)]
    public void DimText_MeasureMatchesRenderedRows(int textLength, int width)
    {
        var text = "[session] " + new string('x', textLength);
        var item = new DimTextItem(text);

        int measured = item.MeasureLineCount(width);

        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, 1000, new DL.DisplayListBuilder().Build(), b);
        int renderedRows = b.Build().Ops.OfType<DL.TextRun>()
            .Where(t => !string.IsNullOrEmpty(t.Content))
            .Select(t => t.Y)
            .Distinct()
            .Count();

        Assert.Equal(measured, renderedRows);
        Assert.True(measured > 1, "Expected the long line to wrap to more than one row.");
    }
}
