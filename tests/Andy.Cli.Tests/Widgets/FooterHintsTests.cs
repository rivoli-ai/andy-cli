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
    public void ShellMode_AdvertisesHowToLeaveAndHowToStopACommand()
    {
        // Issue #286: in shell mode Escape no longer means "quit" on an empty prompt, and Ctrl+C
        // stops the command rather than the app, so both need saying.
        var hints = FooterHints.Build(promptHistoryMode: false, toolOutputExpanded: false,
            mouseOn: false, shellMode: true);

        Assert.Contains(hints, h => h.key == "ESC" && h.action == "Leave shell mode");
        Assert.Contains(hints, h => h.key == "Ctrl+C" && h.action == "Cancel command");
        Assert.Contains(hints, h => h.action == "Shell mode");
        Assert.DoesNotContain(hints, h => h.action == "Quit");
    }

    [Fact]
    public void ShellModeDefaultsOff_SoTheOrdinaryHintsAreUnchanged()
    {
        var hints = FooterHints.Build(promptHistoryMode: false, toolOutputExpanded: false, mouseOn: false);

        Assert.Contains(hints, h => h.key == "ESC" && h.action == "Quit");
    }

    [Fact]
    public void ToolHintReflectsExpandState()
    {
        Assert.Contains(FooterHints.Build(false, true, true), h => h.action == "Collapse output");
        Assert.Contains(FooterHints.Build(false, false, true), h => h.action == "Expand output");
    }
}
