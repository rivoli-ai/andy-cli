using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Themes;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Status/output widgets must paint their text on the active theme background, not a
/// baked black. Under an opaque theme the emitted backgrounds should equal the theme
/// Background; under a transparent theme they should be null so the compositor shows
/// the terminal background. These guard the "thinking / ready / responses on a black
/// block" regressions.
/// </summary>
public class ThemedBackgroundTests
{
    private static readonly DL.Rgb24 OpaqueBg = new DL.Rgb24(12, 34, 56);

    private static DL.DisplayList Render(Theme theme, Action<DL.DisplayListBuilder> draw)
    {
        var original = Theme.Current;
        try
        {
            Theme.Current = theme;
            var b = new DL.DisplayListBuilder();
            draw(b);
            return b.Build();
        }
        finally { Theme.Current = original; }
    }

    private static List<DL.Rgb24?> TextBgs(DL.DisplayList dl) =>
        dl.Ops.OfType<DL.TextRun>().Select(t => t.Bg).ToList();

    private static List<DL.Rgb24?> RectFills(DL.DisplayList dl) =>
        dl.Ops.OfType<DL.Rect>().Select(r => r.Fill).ToList();

    private static Theme Opaque() => new Theme { Name = "t-opaque", Background = OpaqueBg };
    private static Theme Transparent() => new Theme { Name = "t-transparent", Background = null };

    // ----- StatusMessage ("Thinking" / "Ready for next question") -----

    [Fact]
    public void StatusMessage_OpaqueTheme_PaintsThemeBackground()
    {
        var dl = Render(Opaque(), b =>
        {
            var sm = new StatusMessage();
            sm.SetMessage("Thinking", animated: false);
            sm.RenderAt(0, 0, 40, new DL.DisplayListBuilder().Build(), b);
        });
        var bgs = TextBgs(dl);
        Assert.NotEmpty(bgs);
        Assert.All(bgs, bg => Assert.Equal(OpaqueBg, bg));
    }

    // Note: under a transparent theme these widgets fall back to their historic color,
    // which the main-surface compositor strips so the terminal background shows through
    // (verified by the long-standing default dark theme). The regression we guard here
    // is the opaque-theme case, where that fallback would otherwise render as a black block.

    // ----- TokenCounter (footer) -----

    [Fact]
    public void TokenCounter_OpaqueTheme_PaintsThemeBackground()
    {
        var dl = Render(Opaque(), b =>
        {
            var tc = new TokenCounter();
            tc.AddTokens(123, 456);
            tc.RenderAt(0, 0, new DL.DisplayListBuilder().Build(), b);
        });
        var bgs = TextBgs(dl);
        Assert.NotEmpty(bgs);
        Assert.All(bgs, bg => Assert.Equal(OpaqueBg, bg));
    }

    // ----- CommandOutput -----

    [Fact]
    public void CommandOutput_OpaqueTheme_PaintsThemeBackground()
    {
        var dl = Render(Opaque(), b =>
        {
            var co = new CommandOutput();
            co.Append("line one");
            co.Append("line two");
            co.Render(new L.Rect(0, 0, 20, 4), new DL.DisplayListBuilder().Build(), b);
        });
        // Both the fill rect and the text runs use the theme background.
        Assert.Contains(OpaqueBg, RectFills(dl).Select(f => f ?? default));
        Assert.All(TextBgs(dl), bg => Assert.Equal(OpaqueBg, bg));
    }

    // ----- Feed list/separator items -----

    [Fact]
    public void ModelListItem_OpaqueTheme_PaintsThemeBackground()
    {
        var dl = Render(Opaque(), b =>
        {
            var item = new ModelListItem("Available Models");
            item.RenderSlice(0, 0, 40, 0, 5, new DL.DisplayListBuilder().Build(), b);
        });
        var bgs = TextBgs(dl);
        Assert.NotEmpty(bgs);
        Assert.All(bgs, bg => Assert.Equal(OpaqueBg, bg));
    }

    [Fact]
    public void ToolListItem_OpaqueTheme_PaintsThemeBackground()
    {
        var dl = Render(Opaque(), b =>
        {
            var item = new ToolListItem("Available Tools");
            item.RenderSlice(0, 0, 40, 0, 5, new DL.DisplayListBuilder().Build(), b);
        });
        var bgs = TextBgs(dl);
        Assert.NotEmpty(bgs);
        Assert.All(bgs, bg => Assert.Equal(OpaqueBg, bg));
    }

    [Fact]
    public void ResponseSeparator_OpaqueTheme_PaintsThemeBackground()
    {
        var dl = Render(Opaque(), b =>
        {
            var sep = new ResponseSeparatorItem(inputTokens: 10, outputTokens: 20);
            sep.RenderSlice(0, 0, 40, 0, 1, new DL.DisplayListBuilder().Build(), b);
        });
        var bgs = TextBgs(dl);
        Assert.NotEmpty(bgs);
        Assert.All(bgs, bg => Assert.Equal(OpaqueBg, bg));
    }
}
