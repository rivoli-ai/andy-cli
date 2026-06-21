using System.Linq;
using System.Reflection;
using Andy.Cli.Input;
using Xunit;

namespace Andy.Cli.Tests.Input;

/// <summary>
/// Pins the mouse-capture default. Capture defaults to ON so the mouse wheel scrolls the feed out
/// of the box; text selection while capture is on is done with Option+drag (macOS) / Shift+drag
/// (xterm), and F3 toggles capture off for plain click-drag native selection. These tests pin the
/// app-level default and the MouseReporting invariant that nothing is enabled until something
/// explicitly opts in (the constructor itself never auto-enables).
/// </summary>
public class MouseDefaultRegressionTests
{
    [Fact]
    public void TryStart_DefaultsMouseCaptureOn()
    {
        var method = typeof(RawTerminalInput).GetMethod(
            "TryStart", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var param = method!.GetParameters().Single(p => p.Name == "enableMouse");
        Assert.True(param.HasDefaultValue, "enableMouse must have a default so the wheel works out of the box");
        Assert.Equal(true, param.DefaultValue);
    }

    [Fact]
    public void FreshMouseReporting_DoesNotEnableCapture_SoSelectionWorks()
    {
        // Constructing the input layer without opting in must NOT emit the enable sequence;
        // otherwise the terminal would start forwarding mouse events and suppress selection.
        var writes = new System.Collections.Generic.List<string>();
        var mouse = new MouseReporting(writes.Add);

        Assert.False(mouse.Enabled);
        Assert.DoesNotContain(MouseReporting.EnableSeq, writes);
    }
}
