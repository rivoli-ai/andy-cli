using System.Linq;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// The bundled Andy.Tui MarkdownRenderer emits the Underline cell attribute for emphasized text
/// (and the Link widget underlines OSC8 links). The user finds the resulting underlines noisy, so
/// MarkdownRendererItem / StreamingMessageItem / KeyHintsBar post-process the rendered ops to drop
/// Underline and replace it with Bold + a distinct link color. These tests assert no rendered
/// TextRun carries the Underline flag and that emphasized/link text comes out Bold instead.
/// </summary>
public class NoUnderlineRenderingTests
{
    private const DL.CellAttrFlags UnderlineMask = DL.CellAttrFlags.Underline | DL.CellAttrFlags.DoubleUnderline;

    private static System.Collections.Generic.List<DL.TextRun> RenderMarkdown(string md, int width = 90)
    {
        var item = new MarkdownRendererItem(md);
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, item.MeasureLineCount(width), new DL.DisplayListBuilder().Build(), b);
        return b.Build().Ops.OfType<DL.TextRun>()
            .Where(r => !string.IsNullOrEmpty(r.Content)).ToList();
    }

    [Fact]
    public void MarkdownWithLinkAndEmphasis_ProducesNoUnderlinedText()
    {
        var md = "See [the docs](https://example.com/path) and visit https://example.com plus "
               + "*emphasis* and **bold** text.";

        var runs = RenderMarkdown(md);

        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Equal(DL.CellAttrFlags.None, r.Attrs & UnderlineMask));
    }

    [Fact]
    public void MarkdownEmphasis_IsRenderedInAccentColor_NotBold_NotUnderlined()
    {
        // The bundled renderer maps *emphasis* to the Underline attribute. After post-processing
        // those runs are recolored to the theme accent (link) color WITHOUT bold - emphasis/links
        // stand out by color, not weight, so the feed isn't flooded with bold.
        var accent = Andy.Cli.Themes.Theme.Current.Accent;
        var runs = RenderMarkdown("Some text with *emphasized words here* in the middle.");

        // The converted (formerly underlined) runs are the ones now in the accent color.
        var accented = runs.Where(r => r.Fg.HasValue && r.Fg.Value.Equals(accent)).ToList();
        Assert.NotEmpty(accented);
        Assert.All(accented, r =>
        {
            Assert.Equal(DL.CellAttrFlags.None, r.Attrs & UnderlineMask);
            Assert.False(r.Attrs.HasFlag(DL.CellAttrFlags.Bold), "emphasis should be color-only, not bold");
        });
    }

    [Fact]
    public void SimpleHtmlLink_RendersBoldWithoutUnderline()
    {
        // Whole-content <a href> takes the Link (OSC8) path, which underlines by default.
        var item = new MarkdownRendererItem("<a href=\"https://example.com\">click here</a>");
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 80, 0, item.MeasureLineCount(80), new DL.DisplayListBuilder().Build(), b);

        var runs = b.Build().Ops.OfType<DL.TextRun>()
            .Where(r => !string.IsNullOrEmpty(r.Content)).ToList();

        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Equal(DL.CellAttrFlags.None, r.Attrs & UnderlineMask));
    }

    [Fact]
    public void StreamingMessage_WithEmphasis_ProducesNoUnderlinedText()
    {
        var item = new StreamingMessageItem();
        item.AppendContent("Streaming *emphasis* and a link https://example.com here.");

        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 90, 0, item.MeasureLineCount(90) + 4, new DL.DisplayListBuilder().Build(), b);

        var runs = b.Build().Ops.OfType<DL.TextRun>()
            .Where(r => !string.IsNullOrEmpty(r.Content)).ToList();

        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Equal(DL.CellAttrFlags.None, r.Attrs & UnderlineMask));
    }

    [Fact]
    public void KeyHintsBar_UrlHint_RendersBoldWithoutUnderline()
    {
        var hints = new KeyHintsBar();
        hints.SetHints(new[]
        {
            ("Ctrl+P", "Commands"),
            ("", "https://localhost:5555")
        });

        var b = new DL.DisplayListBuilder();
        hints.Render((Width: 120, Height: 24), new DL.DisplayListBuilder().Build(), b, 10);

        var urlRuns = b.Build().Ops.OfType<DL.TextRun>()
            .Where(r => !string.IsNullOrEmpty(r.Content) && r.Content.Contains("https://")).ToList();

        Assert.NotEmpty(urlRuns);
        Assert.All(urlRuns, r =>
        {
            Assert.Equal(DL.CellAttrFlags.None, r.Attrs & UnderlineMask);
            Assert.True(r.Attrs.HasFlag(DL.CellAttrFlags.Bold), "URL hint should be bold");
        });
    }
}
