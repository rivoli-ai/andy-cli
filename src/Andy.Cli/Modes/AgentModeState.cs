using System;

namespace Andy.Cli.Modes;

/// <summary>
/// Who is asking for a mode change. Only <see cref="UserCommand"/> counts as the "explicit user
/// action" that issue #278 requires for leaving Plan mode.
/// </summary>
public enum ModeChangeSource
{
    /// <summary>Process start-up (command-line flag or environment default).</summary>
    Startup,

    /// <summary>The user typed <c>/mode ...</c> (or used the command palette entry).</summary>
    UserCommand,

    /// <summary>Restoring the mode recorded in a saved session.</summary>
    SessionRestore,

    /// <summary>A headless run selecting its mode from <c>--mode</c>.</summary>
    HeadlessConfig,
}

/// <summary>Payload for <see cref="AgentModeState.ModeChanged"/>.</summary>
public sealed class AgentModeChangedEventArgs : EventArgs
{
    public AgentModeChangedEventArgs(
        AgentModeDefinition previous,
        AgentModeDefinition current,
        ModeChangeSource source)
    {
        Previous = previous;
        Current = current;
        Source = source;
    }

    public AgentModeDefinition Previous { get; }
    public AgentModeDefinition Current { get; }
    public ModeChangeSource Source { get; }
}

/// <summary>
/// The process-wide current mode. Registered as a singleton by
/// <c>CliPermissionServiceExtensions.AddAndyCliPermissions</c>, so the permission overlay, the
/// status line, the system prompt and the session writer all read one value.
///
/// Transition rule (issue #278): entering Plan is always allowed, from any source. LEAVING Plan
/// for a mutation-capable mode requires <see cref="ModeChangeSource.UserCommand"/> - restoring a
/// session, a start-up flag, or any programmatic path cannot silently re-enable writes for a user
/// who is currently planning.
/// </summary>
public sealed class AgentModeState
{
    private readonly object _gate = new();
    private AgentMode _current;

    public AgentModeState(AgentMode initial = AgentMode.Build)
    {
        _current = initial;
    }

    /// <summary>The active mode.</summary>
    public AgentMode Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The active mode's definition.</summary>
    public AgentModeDefinition CurrentDefinition => AgentModeCatalog.Get(Current);

    /// <summary>True when the active mode forbids mutation (Plan).</summary>
    public bool IsReadOnly => !CurrentDefinition.AllowsMutation;

    /// <summary>Raised after a change actually took effect. Never raised for a no-op set.</summary>
    public event EventHandler<AgentModeChangedEventArgs>? ModeChanged;

    /// <summary>
    /// Attempts to switch modes.
    /// </summary>
    /// <returns>
    /// True when the mode is now <paramref name="mode"/> (including the no-op case where it
    /// already was). False when the transition was refused, with <paramref name="error"/> set to
    /// a user-facing explanation.
    /// </returns>
    public bool TrySet(AgentMode mode, ModeChangeSource source, out string? error)
    {
        error = null;
        AgentModeChangedEventArgs? changed = null;

        lock (_gate)
        {
            if (_current == mode)
            {
                return true;
            }

            var previous = AgentModeCatalog.Get(_current);
            var target = AgentModeCatalog.Get(mode);

            // Leaving a read-only mode for a mutation-capable one is a privilege escalation and
            // must be a deliberate act by the person at the keyboard.
            if (!previous.AllowsMutation && target.AllowsMutation && source != ModeChangeSource.UserCommand)
            {
                error =
                    $"{previous.DisplayName} mode can only be left by an explicit user action; "
                    + $"run '/mode {target.Id}' to switch to {target.DisplayName} mode.";
                return false;
            }

            _current = mode;
            changed = new AgentModeChangedEventArgs(previous, target, source);
        }

        ModeChanged?.Invoke(this, changed);
        return true;
    }
}
