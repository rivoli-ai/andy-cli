using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Regression for the "markdown shows literal ** and backticks" bug: a model that opens a ``` code
/// fence and never closes it makes the renderer treat everything after it as code, so bold and
/// inline-code markers in the trailing prose render literally. AddMarkdownRich now balances a
/// dangling fence so the prose after it renders as markdown.
/// </summary>
public class UnclosedCodeFenceTests
{
    private static (bool literalStars, bool literalTicks) RenderRich(string md)
    {
        var feed = new FeedView();
        feed.AddMarkdownRich(md);
        var b = new DL.DisplayListBuilder();
        feed.Render(new L.Rect(0, 0, 96, 80), new DL.DisplayListBuilder().Build(), b);
        var runs = b.Build().Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrEmpty(r.Content)).ToList();
        return (runs.Any(r => r.Content.Contains('*')), runs.Any(r => r.Content.Contains('`')));
    }

    [Theory]
    [InlineData("Here:\n```diff\n+ added\n- removed\n\n**Fix scope**: swap to `ConcurrentDictionary<>` now.\n")]
    [InlineData("```csharp\nvar x = 1;\n\n**Fix scope**: do `things`.\n")]
    public void UnclosedFence_DoesNotSwallowTrailingProseAsCode(string md)
    {
        var (stars, ticks) = RenderRich(md);
        Assert.False(stars, "bold markers should be consumed, not rendered literally");
        Assert.False(ticks, "inline-code backticks should be consumed, not rendered literally");
    }

    [Fact]
    public void ClosedFence_StillRendersCodeBlockAndBoldAfter()
    {
        // A properly-closed fence must keep working (the code block is preserved; prose after bolds).
        var (stars, ticks) = RenderRich("```\ncode\n```\n\n**Fix scope**: do `things`.\n");
        Assert.False(stars);
        Assert.False(ticks);
    }

    [Fact]
    public void BalanceCodeFences_DropsTheUnmatchedFence_WhenOdd()
    {
        var balanced = FeedMarkdown.BalanceCodeFences("intro\n```diff\n+ a\n**Bold** after\n");
        Assert.DoesNotContain("```", balanced);
        Assert.Contains("**Bold** after", balanced);
    }

    [Fact]
    public void BalanceCodeFences_LeavesBalancedFencesUnchanged()
    {
        const string md = "intro\n```diff\n+ a\n```\n**Bold** after\n";
        Assert.Equal(md, FeedMarkdown.BalanceCodeFences(md));
    }

    [Fact]
    public void BalanceCodeFences_NoFences_Unchanged()
    {
        const string md = "Just **bold** and `code`, no fences.";
        Assert.Equal(md, FeedMarkdown.BalanceCodeFences(md));
    }
}
