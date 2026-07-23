namespace Andy.Cli.Input;

/// <summary>
/// Decides when mouse capture should be auto-released so the terminal's native
/// click-drag text selection works while the user is scrolled up reading the
/// transcript, and when capture should be restored afterwards.
///
/// Background: while SGR mouse reporting (capture) is on, the terminal forwards
/// mouse events to the app and suppresses native selection. Historically the
/// user had to press F3 to turn capture off before selecting text. This gate
/// implements a pragmatic middle ground:
///
///  - When a left-button press arrives while capture is on AND the feed is
///    scrolled up, capture is released. The press that triggered the release is
///    consumed by the app (the terminal has already sent it to us), so the
///    selection itself starts with the user's NEXT click-drag.
///  - Once the feed returns to the bottom (PageDown/End, typing snapping the
///    view, or auto-scroll resuming), capture is restored automatically so the
///    wheel scrolls the feed again.
///  - A manual F3 toggle takes precedence: it clears the auto-release state so
///    the gate never fights an explicit user choice.
///
/// Pure state machine (no terminal I/O) so it is unit-testable; the caller owns
/// the actual <see cref="MouseReporting"/> writes.
/// </summary>
public sealed class ScrollSelectionGate
{
    /// <summary>True while capture is off because this gate released it (as opposed
    /// to the user toggling it off with F3).</summary>
    public bool AutoReleased { get; private set; }

    /// <summary>
    /// Called when a left mouse button press is decoded. Returns true when the
    /// caller should turn mouse capture off so the user's next click-drag
    /// performs native terminal text selection.
    /// </summary>
    /// <param name="captureEnabled">Current mouse reporting state.</param>
    /// <param name="scrolledUp">True when the feed is scrolled away from the bottom.</param>
    public bool OnMouseDown(bool captureEnabled, bool scrolledUp)
    {
        if (!captureEnabled || !scrolledUp) return false;
        AutoReleased = true;
        return true;
    }

    /// <summary>
    /// Called periodically (once per main-loop iteration). Returns true when the
    /// caller should turn mouse capture back on: the gate had auto-released it
    /// and the feed is back at the bottom.
    /// </summary>
    /// <param name="atBottom">True when the feed is at the bottom (offset 0).</param>
    public bool OnTick(bool atBottom)
    {
        if (!AutoReleased || !atBottom) return false;
        AutoReleased = false;
        return true;
    }

    /// <summary>
    /// Called when the user toggles mouse capture manually (F3). Manual control
    /// overrides any pending auto-release so the gate does not re-enable capture
    /// behind the user's back.
    /// </summary>
    public void OnManualToggle() => AutoReleased = false;
}
