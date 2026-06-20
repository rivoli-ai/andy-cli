using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Regression guards for two feed-rendering artifacts that have recurred historically:
///  - a bold run leaking across an entire paragraph with the literal "**" markers still visible,
///    triggered by bold spans that contain quotes/parens/'='/digits (e.g. an audit list item); and
///  - the "dot n" artifact, where a spurious "* n" (bullet + n) prefix appears on feed lines.
/// Both are checked against the actual feed renderers so a future regression fails here.
/// </summary>
public class MarkdownRenderingRegressionTests
{
    private static System.Collections.Generic.List<DL.TextRun> RenderItem(string md, int width)
    {
        var item = new MarkdownRendererItem(md);
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, item.MeasureLineCount(width), new DL.DisplayListBuilder().Build(), b);
        return b.Build().Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrEmpty(r.Content)).ToList();
    }

    [Theory]
    [InlineData("2. **\"Rendering System Architecture Issues\" section (dated 2025-08-11)**: References VirtualDomRenderer.")]
    [InlineData("7. **The entire document describes a multi-iteration loop with 'MaxIterations = 12'** - the current model.")]
    [InlineData("6. **Feature list incomplete**: Missing headless agent runtime, ACP server mode.")]
    public void BoldSpanWithSpecialChars_DoesNotLeakOrShowLiteralStars(string md)
    {
        foreach (var width in new[] { 48, 70, 95 })
        {
            var runs = RenderItem(md, width);

            // The "**" markers must be consumed by the parser, never rendered literally.
            Assert.DoesNotContain(runs, r => r.Content.Contains('*'));

            // Bold must not cover the whole paragraph: the text after the closing "**" stays plain.
            var nonBold = string.Concat(runs
                .Where(r => !r.Attrs.HasFlag(DL.CellAttrFlags.Bold))
                .OrderBy(r => r.Y).ThenBy(r => r.X).Select(r => r.Content));
            Assert.False(string.IsNullOrWhiteSpace(nonBold),
                $"width={width}: expected some non-bold trailing text, whole paragraph was bold");
        }
    }

    [Fact]
    public void Feed_RendersHelpThemeAndModelOutput_WithoutDotNArtifact()
    {
        var feed = new FeedView();
        feed.AddMarkdownRich("Theme switched to 'gruvbox-light'.");
        feed.AddMarkdownRich("Theme switched to 'monokai'.");
        feed.AddMarkdownRich(
            "Models - openrouter: xiaomi/mimo-v2.5\n" +
            "cerebras [https://api.cerebras.ai]:\n" +
            "anthropic [https://api.anthropic.com] (no key):");
        feed.AddMarkdownRich(
            "# Andy CLI Help\n\n## Keyboard Shortcuts:\n" +
            "- **Ctrl+P** (Cmd+P on Mac): Open command palette\n" +
            "- **F2**: Toggle HUD (performance overlay)\n" +
            "- **F3**: Toggle mouse capture (off = native text selection; on = mouse-wheel scroll)\n" +
            "- **Ctrl+U**: Delete from start of line to cursor\n\n" +
            "## Commands\n\n### General Commands:\n" +
            "- **/model list**: Show available models\n");

        foreach (var (W, H) in new[] { (96, 40), (70, 18) })
        {
            // Two passes: first establishes layout, second is the asserted frame.
            feed.Render(new L.Rect(0, 0, W, H), new DL.DisplayListBuilder().Build(), new DL.DisplayListBuilder());
            var b = new DL.DisplayListBuilder();
            feed.Render(new L.Rect(0, 0, W, H), new DL.DisplayListBuilder().Build(), b);

            // Reconstruct each rendered row and assert none carries the spurious "<bullet> n" prefix.
            // None of our content has a bullet item beginning with "n", so "• n" can only be the artifact.
            var rows = b.Build().Ops.OfType<DL.TextRun>()
                .Where(r => !string.IsNullOrEmpty(r.Content))
                .GroupBy(r => r.Y)
                .Select(g => string.Concat(g.OrderBy(r => r.X).Select(r => r.Content)));
            foreach (var row in rows)
            {
                Assert.DoesNotContain("• n", row);
                Assert.DoesNotContain("•n", row);
            }
        }
    }
}
