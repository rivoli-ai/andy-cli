using System.Linq;
using System.Reflection;
using Andy.Cli.Input;
using Xunit;

namespace Andy.Cli.Tests.Input;

/// <summary>
/// Pins the mouse-capture default. Capture defaults to OFF so plain click-drag selection and the
/// terminal's copy shortcut work without a preparatory click or modifier. F3 toggles capture on
/// when mouse-wheel scrolling is preferred. These tests pin the
/// app-level default and the MouseReporting invariant that nothing is enabled until something
/// explicitly opts in (the constructor itself never auto-enables).
/// </summary>
public class MouseDefaultRegressionTests
{
    [Fact]
    public void TryStart_DefaultsMouseCaptureOff()
    {
        var method = typeof(RawTerminalInput).GetMethod(
            "TryStart", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var param = method!.GetParameters().Single(p => p.Name == "enableMouse");
        Assert.True(param.HasDefaultValue, "enableMouse must have an explicit selection-safe default");
        Assert.Equal(false, param.DefaultValue);
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
