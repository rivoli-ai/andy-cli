using System.Linq;
using System.Reflection;
using Andy.Cli.Input;
using Xunit;

namespace Andy.Cli.Tests.Input;

/// <summary>
/// Regression guard: mouse capture must default to OFF so the terminal's native click-drag text
/// selection and Cmd+A/Cmd+C copy keep working out of the box. Turning the default on (as was once
/// tried) silently breaks selection because mouse reporting makes the terminal forward mouse events
/// to the app instead of selecting text. These tests pin both the app-level default and the
/// MouseReporting invariant that nothing is enabled until something explicitly opts in.
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
        Assert.True(param.HasDefaultValue, "enableMouse must have a default so callers get selection-safe behavior");
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
