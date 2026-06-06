using System.Linq;
using Andy.Cli.Themes;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Verifies that the text the user types into the prompt stays readable across
/// themes regardless of the terminal background. Because the application cannot
/// reliably detect the terminal background, the prompt text is drawn with the
/// terminal's own default foreground color (<see cref="Theme.PromptText"/> is
/// null, which the encoder emits as ANSI SGR 39). That default is inherently
/// legible against the terminal's own background, so no theme forces an explicit
/// RGB color that could clash with an unexpected terminal background.
/// </summary>
public class PromptLineColorTests
{
    private static DL.TextRun? FindRun(DL.DisplayList dl, string content)
    {
        foreach (var op in dl.Ops)
        {
            if (op is DL.TextRun run && run.Content == content)
                return run;
        }
        return null;
    }

    private static DL.DisplayList RenderWith(Theme theme, string text)
    {
        var original = Theme.Current;
        try
        {
            Theme.Current = theme;
            var prompt = new PromptLine();
            prompt.SetText(text);

            var baseBuilder = new DL.DisplayListBuilder();
            var baseDl = baseBuilder.Build();
            var builder = new DL.DisplayListBuilder();

            var rect = new L.Rect(0, 0, 40, 5);
            prompt.Render(rect, baseDl, builder);
            return builder.Build();
        }
        finally
        {
            Theme.Current = original;
        }
    }

    [Fact]
    public void Render_UsesPromptTextColorForTypedText_Dark()
    {
        var dl = RenderWith(Theme.Dark, "hello");

        var run = FindRun(dl, "hello");
        Assert.NotNull(run);
        // PromptText is the terminal default (null) so the foreground is left unset.
        Assert.Equal(Theme.Dark.PromptText, run!.Value.Fg);
        // The typed text must not fall back to the accent color.
        Assert.NotEqual(Theme.Dark.Primary, run!.Value.Fg);
    }

    [Fact]
    public void Render_UsesPromptTextColorForTypedText_Light()
    {
        var dl = RenderWith(Theme.Light, "hello");

        var run = FindRun(dl, "hello");
        Assert.NotNull(run);
        Assert.Equal(Theme.Light.PromptText, run!.Value.Fg);
        Assert.NotEqual(Theme.Light.Primary, run!.Value.Fg);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void PromptText_UsesTerminalDefaultForeground(string themeName)
    {
        var theme = Theme.GetByName(themeName)!;

        // A null foreground is emitted as ANSI SGR 39 (terminal default fg),
        // which is the only choice guaranteed to be readable on the user's
        // actual terminal background, which the app cannot detect.
        Assert.Null(theme.PromptText);

        // The typed text run must carry that unset (terminal default) foreground.
        var dl = RenderWith(theme, "hello");
        var run = FindRun(dl, "hello");
        Assert.NotNull(run);
        Assert.Null(run!.Value.Fg);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void PromptField_ForegroundAndBackgroundContrastByConstruction(string themeName)
    {
        var theme = Theme.GetByName(themeName)!;

        // The prompt field is a self-contained, readable pair by construction:
        // both foreground (PromptText) and background (PromptBackground) defer to
        // the terminal's own default fg/bg, which the terminal guarantees to be a
        // legible, contrasting pair. Neither forces an RGB color that could clash.
        Assert.Null(theme.PromptText);
        Assert.Null(theme.PromptBackground);
    }
}
