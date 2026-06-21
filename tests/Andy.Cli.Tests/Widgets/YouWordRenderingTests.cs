using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Regression: the markdown feed item used to inject a zero-width non-joiner (U+200C) into the word
/// "You" as a workaround for an old renderer quirk. That character rendered as a visible space in
/// some terminals ("Y ou"). The current renderer handles "You" normally, so the word must render
/// intact with no injected control character.
/// </summary>
public class YouWordRenderingTests
{
    private static string RenderText(string md)
    {
        var item = new MarkdownRendererItem(md);
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 80, 0, item.MeasureLineCount(80), new DL.DisplayListBuilder().Build(), b);
        return string.Concat(b.Build().Ops.OfType<DL.TextRun>()
            .OrderBy(r => r.Y).ThenBy(r => r.X).Select(r => r.Content));
    }

    [Theory]
    [InlineData("You raise a good point.")]
    [InlineData("Thanks. You were right about that.")]
    [InlineData("- You can also do this.")]
    public void You_RendersIntact_WithoutInjectedZeroWidthCharacter(string md)
    {
        var text = RenderText(md);
        Assert.DoesNotContain('\u200C', text);             // no zero-width non-joiner
        Assert.Contains("You ", text);                     // "You" + space renders as written
    }
}
