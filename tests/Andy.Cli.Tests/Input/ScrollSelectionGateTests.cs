using System.Collections.Generic;
using Andy.Cli.Input;
using Xunit;

namespace Andy.Cli.Tests.Input;

/// <summary>
/// Tests for <see cref="ScrollSelectionGate"/> (issue #230): a click in the
/// transcript while the user is scrolled up releases mouse capture so the
/// terminal's native click-drag text selection works without pressing F3, and
/// capture is restored automatically once the feed is back at the bottom. A
/// manual F3 toggle always wins over the auto-release.
/// </summary>
public class ScrollSelectionGateTests
{
    [Fact]
    public void MouseDown_WhileCapturedAndScrolledUp_ReleasesCapture()
    {
        var gate = new ScrollSelectionGate();

        Assert.True(gate.OnMouseDown(captureEnabled: true, scrolledUp: true));
        Assert.True(gate.AutoReleased);
    }

    [Fact]
    public void MouseDown_AtBottom_DoesNotRelease()
    {
        var gate = new ScrollSelectionGate();

        Assert.False(gate.OnMouseDown(captureEnabled: true, scrolledUp: false));
        Assert.False(gate.AutoReleased);
    }

    [Fact]
    public void MouseDown_WhenCaptureAlreadyOff_DoesNotClaimRelease()
    {
        // Capture off means the user turned it off (F3) or it was never on;
        // the gate must not take ownership of that state.
        var gate = new ScrollSelectionGate();

        Assert.False(gate.OnMouseDown(captureEnabled: false, scrolledUp: true));
        Assert.False(gate.AutoReleased);
    }

    [Fact]
    public void Tick_AtBottomAfterAutoRelease_RestoresCaptureOnce()
    {
        var gate = new ScrollSelectionGate();
        gate.OnMouseDown(captureEnabled: true, scrolledUp: true);

        Assert.True(gate.OnTick(atBottom: true));
        Assert.False(gate.AutoReleased);
        // Idempotent: a second tick must not re-enable again.
        Assert.False(gate.OnTick(atBottom: true));
    }

    [Fact]
    public void Tick_WhileStillScrolledUp_DoesNotRestore()
    {
        var gate = new ScrollSelectionGate();
        gate.OnMouseDown(captureEnabled: true, scrolledUp: true);

        Assert.False(gate.OnTick(atBottom: false));
        Assert.True(gate.AutoReleased); // still pending until back at bottom
    }

    [Fact]
    public void Tick_WithoutAutoRelease_NeverRestores()
    {
        var gate = new ScrollSelectionGate();
        Assert.False(gate.OnTick(atBottom: true));
    }

    [Fact]
    public void ManualToggle_ClearsAutoRelease_SoTickDoesNotFightTheUser()
    {
        var gate = new ScrollSelectionGate();
        gate.OnMouseDown(captureEnabled: true, scrolledUp: true);

        // The user presses F3 while the gate had auto-released capture: the
        // manual choice wins and the gate must not re-enable capture later.
        gate.OnManualToggle();

        Assert.False(gate.AutoReleased);
        Assert.False(gate.OnTick(atBottom: true));
    }

    [Fact]
    public void FullCycle_DrivesMouseReportingSequences()
    {
        // Integration with the MouseReporting state machine via a fake sink:
        // capture on -> click while scrolled up releases -> back at bottom restores.
        var writes = new List<string>();
        var mouse = new MouseReporting(writes.Add);
        mouse.Set(true);

        var gate = new ScrollSelectionGate();
        if (gate.OnMouseDown(mouse.Enabled, scrolledUp: true)) mouse.Set(false);
        Assert.False(mouse.Enabled);

        if (gate.OnTick(atBottom: true)) mouse.Set(true);
        Assert.True(mouse.Enabled);

        Assert.Equal(
            new[] { MouseReporting.EnableSeq, MouseReporting.DisableSeq, MouseReporting.EnableSeq },
            writes);
    }
}
