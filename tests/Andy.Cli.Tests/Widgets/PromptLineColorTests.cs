using System.Linq;
using Andy.Cli.Themes;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Verifies that the text the user types into the prompt is rendered with the
/// dedicated, high-contrast <see cref="Theme.PromptText"/> color rather than the
/// lower-contrast accent color, and that the choice stays readable across themes.
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

    // Approximate WCAG relative luminance for an sRGB color (0..1).
    private static double Luminance(DL.Rgb24 c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double ContrastRatio(DL.Rgb24 a, DL.Rgb24 b)
    {
        double la = Luminance(a) + 0.05;
        double lb = Luminance(b) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void PromptText_HasHigherContrastThanAccent_AgainstTypicalBackground(string themeName)
    {
        var theme = Theme.GetByName(themeName)!;

        // PromptBackground is transparent (null); contrast is judged against the
        // terminal surface the prompt sits on. Use the worst-case terminal
        // background for each theme: black for dark, white for light.
        var surface = themeName == "dark"
            ? new DL.Rgb24(0, 0, 0)
            : new DL.Rgb24(255, 255, 255);

        double promptContrast = ContrastRatio(theme.PromptText, surface);
        double accentContrast = ContrastRatio(theme.Primary, surface);

        // The chosen prompt color must be at least as readable as the accent,
        // and comfortably above the WCAG AA body-text threshold of 4.5:1.
        Assert.True(promptContrast >= accentContrast,
            $"PromptText contrast {promptContrast:F2} should be >= accent contrast {accentContrast:F2} for theme '{themeName}'");
        Assert.True(promptContrast >= 4.5,
            $"PromptText contrast {promptContrast:F2} should meet WCAG AA (4.5:1) for theme '{themeName}'");
    }
}
