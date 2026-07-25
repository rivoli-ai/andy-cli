using System.Linq;
using Andy.Cli.Themes;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// ANSI decoding tests (#250). Before this existed, escape bytes from colorizing tools
/// (ls --color, dotnet build, git --color) were pushed into the display list verbatim.
/// </summary>
public class AnsiTextTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void PlainTextIsOneSpan()
    {
        var line = AnsiText.Decode("hello world");

        Assert.Equal("hello world", line.Text);
        Assert.Single(line.Spans);
    }

    [Fact]
    public void ColorSequencesBecomeSpansAndNeverReachTheText()
    {
        var line = AnsiText.Decode($"{Esc}[31merror{Esc}[0m: something broke");

        // The escape bytes are gone from the rendered characters.
        Assert.Equal("error: something broke", line.Text);
        Assert.Equal(2, line.Spans.Count);
        Assert.Equal(Theme.Current.Error, line.Spans[0].Foreground);
        Assert.Null(line.Spans[1].Foreground);
    }

    [Fact]
    public void BasicColorsMapOntoThemeRoles()
    {
        // A program printing red means "bad"; the theme's error color says that on any background.
        Assert.Equal(Theme.Current.Error, AnsiText.Decode($"{Esc}[31mx").Spans[0].Foreground);
        Assert.Equal(Theme.Current.Success, AnsiText.Decode($"{Esc}[32mx").Spans[0].Foreground);
        Assert.Equal(Theme.Current.Warning, AnsiText.Decode($"{Esc}[33mx").Spans[0].Foreground);
        Assert.Equal(Theme.Current.Info, AnsiText.Decode($"{Esc}[34mx").Spans[0].Foreground);
    }

    [Fact]
    public void BoldAndItalicBecomeAttributes()
    {
        var line = AnsiText.Decode($"{Esc}[1mbold{Esc}[22m plain");

        Assert.True(line.Spans[0].Attributes.HasFlag(Andy.Tui.DisplayList.CellAttrFlags.Bold));
        Assert.False(line.Spans[1].Attributes.HasFlag(Andy.Tui.DisplayList.CellAttrFlags.Bold));
    }

    [Fact]
    public void TruecolorAndIndexedColorsUseTheirLiteralValue()
    {
        var truecolor = AnsiText.Decode($"{Esc}[38;2;10;20;30mx");
        Assert.Equal(new Andy.Tui.DisplayList.Rgb24(10, 20, 30), truecolor.Spans[0].Foreground);

        // 256-color index 231 is the top of the RGB cube (white).
        var indexed = AnsiText.Decode($"{Esc}[38;5;231mx");
        Assert.Equal(new Andy.Tui.DisplayList.Rgb24(255, 255, 255), indexed.Spans[0].Foreground);
    }

    [Fact]
    public void NonSgrSequencesAreDiscardedEntirely()
    {
        // Cursor movement and screen erase are instructions to a real terminal; they must not
        // print, and must not eat the surrounding text.
        var line = AnsiText.Decode($"before{Esc}[2K{Esc}[1;1Hafter");

        Assert.Equal("beforeafter", line.Text);
    }

    [Fact]
    public void OperatingSystemCommandsAreConsumedWhole()
    {
        // OSC 0 (set window title) terminated by BEL.
        var line = AnsiText.Decode($"{Esc}]0;my title\avisible");

        Assert.Equal("visible", line.Text);
    }

    [Fact]
    public void CarriageReturnRewritesTheRowRatherThanBreakingIt()
    {
        // A progress bar would otherwise explode into one feed line per update.
        var line = AnsiText.Decode("10%\r50%\r100%");

        Assert.Equal("100%", line.Text);
    }

    [Fact]
    public void TabsBecomeSpacesSoColumnsStayInSync()
    {
        Assert.Equal("a    b", AnsiText.Decode("a\tb").Text);
    }

    [Fact]
    public void TruncatedSequenceAtEndOfLineDoesNotLeak()
    {
        var line = AnsiText.Decode($"text{Esc}[3");

        Assert.Equal("text", line.Text);
    }

    [Fact]
    public void StripRemovesEverySequence()
    {
        Assert.Equal("error: broke",
            AnsiText.Strip($"{Esc}[31merror{Esc}[0m: broke"));
    }

    [Fact]
    public void DecodeLinesSplitsOnNewlinesOnly()
    {
        var lines = AnsiText.DecodeLines("a\r\nb\nc");

        Assert.Equal(3, lines.Count);
        Assert.Equal(new[] { "a", "b", "c" }, lines.Select(l => l.Text));
    }
}
