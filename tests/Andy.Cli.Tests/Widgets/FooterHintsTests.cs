using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// The footer shows a live Mouse On/Off indicator (F3) in place of the old "[F2] Toggle HUD" hint.
/// </summary>
public class FooterHintsTests
{
    [Fact]
    public void ShowsMouseOn_WhenMouseEnabled()
    {
        var hints = FooterHints.Build(promptHistoryMode: false, toolOutputExpanded: false, mouseOn: true);
        Assert.Contains(hints, h => h.key == "F3" && h.action == "Mouse On");
    }

    [Fact]
    public void ShowsMouseOff_WhenMouseDisabled()
    {
        var hints = FooterHints.Build(promptHistoryMode: false, toolOutputExpanded: false, mouseOn: false);
        Assert.Contains(hints, h => h.key == "F3" && h.action == "Mouse Off");
    }

    [Fact]
    public void DoesNotAdvertiseF2HudInFooter()
    {
        foreach (var mode in new[] { true, false })
        {
            var hints = FooterHints.Build(promptHistoryMode: mode, toolOutputExpanded: false, mouseOn: true);
            Assert.DoesNotContain(hints, h => h.key == "F2");
            Assert.DoesNotContain(hints, h => h.action.Contains("HUD"));
        }
    }

    [Fact]
    public void MouseIndicatorPresentInBothScrollModes()
    {
        Assert.Contains(FooterHints.Build(true, false, true), h => h.key == "F3");
        Assert.Contains(FooterHints.Build(false, false, true), h => h.key == "F3");
    }

    [Fact]
    public void ToolHintReflectsExpandState()
    {
        Assert.Contains(FooterHints.Build(false, true, true), h => h.action == "Collapse output");
        Assert.Contains(FooterHints.Build(false, false, true), h => h.action == "Expand output");
    }
}
