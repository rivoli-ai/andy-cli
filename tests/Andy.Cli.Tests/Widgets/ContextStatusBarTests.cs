using System.Linq;
using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class ContextStatusBarTests
{
    private static ContextStatusBar Bar(int input, int output, int max, int turns,
        string model = "", string provider = "")
    {
        var bar = new ContextStatusBar();
        bar.Update(input, output, max, turns);
        bar.SetModelInfo(model, provider);
        return bar;
    }

    // --- Token usage formatting -------------------------------------------------

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1500, "1.5K")]
    [InlineData(200_000, "200.0K")]
    [InlineData(2_000_000, "2.0M")]
    public void FormatUsage_NoMax_UsesCompactSuffix(int total, string expected)
    {
        var bar = Bar(total, 0, 0, 0);
        Assert.Equal(expected, bar.FormatUsage());
    }

    [Fact]
    public void FormatUsage_WithMax_ShowsUsedSlashMax()
    {
        var bar = Bar(15_000, 200, 200_000, 5);
        Assert.Equal("15.2K / 200.0K", bar.FormatUsage());
    }

    // --- Percentage -------------------------------------------------------------

    [Fact]
    public void FormatPercentage_WithMax_RendersOneDecimal()
    {
        var bar = Bar(15_000, 5_000, 200_000, 5); // 20000 / 200000 = 10%
        Assert.Equal("(10.0%)", bar.FormatPercentage());
    }

    [Fact]
    public void FormatPercentage_WithoutMax_IsEmpty()
    {
        var bar = Bar(15_000, 5_000, 0, 5);
        Assert.Equal("", bar.FormatPercentage());
    }

    // --- Usage level (color thresholds) ----------------------------------------

    [Theory]
    [InlineData(10_000, UsageLevel.Normal)]   // 10%
    [InlineData(140_000, UsageLevel.Warning)] // 70%
    [InlineData(185_000, UsageLevel.Critical)] // 92.5%
    public void Level_TracksThresholds(int total, UsageLevel expected)
    {
        var bar = Bar(total, 0, 200_000, 5);
        Assert.Equal(expected, bar.Level);
    }

    [Fact]
    public void Level_IsNormal_WhenMaxUnknown()
    {
        var bar = Bar(500_000, 0, 0, 5);
        Assert.Equal(UsageLevel.Normal, bar.Level);
    }

    // --- Turns ------------------------------------------------------------------

    [Fact]
    public void FormatTurns_WithMax_EstimatesRemaining()
    {
        // 20K used over 4 turns => 5K/turn; (200K-20K)/5K = 36 remaining.
        var bar = Bar(15_000, 5_000, 200_000, 4);
        Assert.Equal("~36 turns left", bar.FormatTurns());
    }

    [Fact]
    public void FormatTurns_WithoutMax_ShowsElapsedCount()
    {
        Assert.Equal("3 turns", Bar(1000, 0, 0, 3).FormatTurns());
        Assert.Equal("1 turn", Bar(1000, 0, 0, 1).FormatTurns());
    }

    // --- Model name truncation --------------------------------------------------

    [Fact]
    public void TruncateModelName_StripsProviderPrefix()
    {
        Assert.Equal("gpt-4o", ContextStatusBar.TruncateModelName("openai/gpt-4o", 20));
    }

    [Fact]
    public void TruncateModelName_TruncatesLongNamesWithEllipsis()
    {
        var result = ContextStatusBar.TruncateModelName("anthropic/claude-very-long-model-name-here", 20);
        Assert.True(result.Length <= 20);
        Assert.EndsWith("...", result);
    }

    // --- GetWidth ---------------------------------------------------------------

    [Fact]
    public void GetWidth_ReturnsPositive_WithAndWithoutModel()
    {
        Assert.True(Bar(1500, 500, 200_000, 10, "gpt-4o", "openai").GetWidth() > 0);
        Assert.True(Bar(100, 50, 0, 0).GetWidth() > 0);
    }

    [Fact]
    public void SetModelInfo_HandlesNullValues()
    {
        var bar = new ContextStatusBar();
        bar.Update(1500, 500, 200_000, 10);
        bar.SetModelInfo(null, null);
        Assert.True(bar.GetWidth() > 0);
    }

    // --- Rendering --------------------------------------------------------------

    private static DL.DisplayList Render(ContextStatusBar bar, int width = 120, int height = 30)
    {
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        bar.Render((width, height), baseDl, builder);
        return builder.Build();
    }

    private static bool HasRunContaining(DL.DisplayList dl, string text) =>
        dl.Ops.Any(op => op is DL.TextRun run && run.Content.Contains(text));

    [Fact]
    public void Render_DrawsModelAndUsageAndPercentage()
    {
        var bar = Bar(15_000, 5_000, 200_000, 4, "openai/gpt-4o", "openai");
        var dl = Render(bar);

        Assert.True(HasRunContaining(dl, "Model: "));
        Assert.True(HasRunContaining(dl, "gpt-4o"));      // prefix stripped
        Assert.True(HasRunContaining(dl, "(openai)"));
        Assert.True(HasRunContaining(dl, "20.0K / 200.0K"));
        Assert.True(HasRunContaining(dl, "(10.0%)"));
    }

    [Fact]
    public void Render_DegradesGracefully_WhenMaxUnknown()
    {
        var bar = Bar(20_000, 0, 0, 4, "claude", "anthropic");
        var dl = Render(bar);

        // No percentage and no "/max" when the window is unknown.
        Assert.False(HasRunContaining(dl, "%"));
        Assert.False(HasRunContaining(dl, "/"));
        Assert.True(HasRunContaining(dl, "20.0K"));
    }

    [Fact]
    public void Render_TooNarrow_DrawsNothing()
    {
        var bar = Bar(20_000, 0, 200_000, 4, "claude", "anthropic");
        var dl = Render(bar, width: 10);
        Assert.Empty(dl.Ops);
    }
}
