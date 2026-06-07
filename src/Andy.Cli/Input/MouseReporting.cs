using System;

namespace Andy.Cli.Input;

/// <summary>
/// Tracks and toggles SGR mouse reporting for a terminal.
///
/// While mouse reporting is on the terminal forwards mouse events (clicks,
/// drags, wheel) to the application, which suppresses the terminal emulator's
/// native click-drag text selection. Turning it off restores native selection.
/// The CLI only uses mouse events for wheel scrolling, which is also reachable
/// via PageUp/PageDown, so reporting defaults to off and is opt-in.
///
/// The actual terminal writes are routed through an injected sink so the state
/// machine can be unit-tested without a real TTY.
/// </summary>
internal sealed class MouseReporting
{
    // 1000 = button tracking (reports wheel as buttons 64/65); 1006 = SGR
    // extended coordinates. Disable inverts both ('h' -> 'l').
    internal const string EnableSeq = "\u001b[?1000h\u001b[?1006h";
    internal const string DisableSeq = "\u001b[?1000l\u001b[?1006l";

    private readonly Action<string> _write;
    private readonly object _lock = new();
    private bool _enabled;

    public MouseReporting(Action<string> write)
        => _write = write ?? throw new ArgumentNullException(nameof(write));

    /// <summary>Current reporting state.</summary>
    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
    }

    /// <summary>
    /// Set reporting on or off and return the new state. Writes the matching
    /// escape sequence only on an actual state change; a failed write is
    /// swallowed (best effort) but the tracked state still advances.
    /// </summary>
    public bool Set(bool enabled)
    {
        lock (_lock)
        {
            if (enabled == _enabled) return _enabled;
            try { _write(enabled ? EnableSeq : DisableSeq); }
            catch { /* ignore: best effort, terminal may be gone */ }
            _enabled = enabled;
            return _enabled;
        }
    }

    /// <summary>Flip reporting and return the new state.</summary>
    public bool Toggle()
    {
        lock (_lock) return Set(!_enabled);
    }
}
