using System;
using System.Threading;

namespace Andy.Cli.Editor;

/// <summary>
/// Input source that can temporarily hand the TTY back to a child process.
/// Implemented by <see cref="Andy.Cli.Input.RawTerminalInput"/>; kept as an interface so
/// the suspend/restore sequencing can be tested without a real terminal.
/// </summary>
public interface ISuspendableTerminalInput
{
    /// <summary>
    /// Stop reading stdin, disable mouse reporting and put the TTY back into cooked
    /// mode. Disposing the returned scope restores raw mode, mouse state and reading.
    /// Must be safe to call when already suspended.
    /// </summary>
    IDisposable Suspend();
}

/// <summary>
/// Hands the terminal to an external program and takes it back afterwards.
///
/// <para>Suspending, in order: stop raw input (cooked mode restored), disable SGR mouse
/// reporting, re-enable line wrap, show the cursor, leave the alternate screen. Restoring
/// reverses that and asks the host for a full repaint.</para>
///
/// <para>Restoration is the highest-risk part of issue #287, so it is defensive: every
/// individual step is wrapped, the restore path runs exactly once, and the scope hooks
/// both process lifetime events.</para>
///
/// <list type="bullet">
///   <item><description><c>Console.CancelKeyPress</c>: a Ctrl+C typed while the editor owns the
///     terminal is meant for the EDITOR (both processes are in the same foreground group and
///     both receive SIGINT). The scope therefore cancels the default termination
///     (<see cref="TerminalSuspendScope.SuppressCancel"/>) so Andy survives; if the editor died
///     from the signal it reports 128+SIGINT and the normal restore path runs.</description></item>
///   <item><description><c>ProcessExit</c>: the process really is going away, so
///     <see cref="TerminalSuspendScope.Abandon"/> leaves the terminal usable - cursor and wrap
///     on, mouse off - and crucially does NOT re-enter the alternate screen we already left.</description></item>
/// </list>
/// </summary>
public sealed class TerminalSuspendController
{
    /// <summary>Written when handing the terminal to the editor.</summary>
    public const string LeaveTuiSequence = "\u001b[?1000l\u001b[?1006l\u001b[?7h\u001b[?25h\u001b[?1049l";

    /// <summary>Written when taking the terminal back.</summary>
    public const string EnterTuiSequence = "\u001b[?1049h\u001b[?25l\u001b[?7l";

    /// <summary>Written on the emergency path (process is going away; stay out of the alt screen).</summary>
    public const string EmergencySequence = "\u001b[?1000l\u001b[?1006l\u001b[?7h\u001b[?25h";

    private readonly Action<string> _write;
    private readonly ISuspendableTerminalInput? _input;
    private readonly Action? _requestFullRepaint;
    private readonly object _lock = new();
    private TerminalSuspendScope? _active;

    public TerminalSuspendController(
        Action<string> write,
        ISuspendableTerminalInput? input = null,
        Action? requestFullRepaint = null)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _input = input;
        _requestFullRepaint = requestFullRepaint;
    }

    /// <summary>True while the terminal belongs to a child process.</summary>
    public bool IsSuspended => ActiveScope is not null;

    /// <summary>The in-flight hand-off, or null when the TUI owns the terminal.</summary>
    public TerminalSuspendScope? ActiveScope
    {
        get { lock (_lock) return _active is { Completed: false } ? _active : null; }
    }

    /// <summary>
    /// Give the terminal to a child process. Dispose the returned scope to take it back.
    /// Never throws: a failure in any step is swallowed so the caller can still launch and,
    /// more importantly, still restore.
    /// </summary>
    public TerminalSuspendScope Suspend()
    {
        lock (_lock)
        {
            if (_active is { Completed: false }) return _active;

            IDisposable? inputScope = null;
            try { inputScope = _input?.Suspend(); }
            catch { /* best effort: continue and still hand over the screen */ }

            SafeWrite(LeaveTuiSequence);

            var scope = new TerminalSuspendScope(this, inputScope);
            _active = scope;
            return scope;
        }
    }

    internal void Restore(IDisposable? inputScope)
    {
        SafeWrite(EnterTuiSequence);
        try { inputScope?.Dispose(); } catch { /* ignore */ }
        try { _requestFullRepaint?.Invoke(); } catch { /* ignore */ }
    }

    internal void Abandon(IDisposable? inputScope)
    {
        // The process is terminating. Do NOT re-enter the alternate screen; just make sure
        // the cursor is visible, wrapping is on and mouse reporting is off. The input scope
        // restores the saved termios via its own teardown.
        SafeWrite(EmergencySequence);
        try { inputScope?.Dispose(); } catch { /* ignore */ }
    }

    private void SafeWrite(string s)
    {
        try { _write(s); } catch { /* terminal may be gone */ }
    }
}

/// <summary>
/// The lifetime of one terminal hand-off. Disposing restores the TUI;
/// <see cref="Abandon"/> performs the emergency restore used when the process is exiting.
/// Both are idempotent and mutually exclusive - whichever runs first wins.
/// </summary>
public sealed class TerminalSuspendScope : IDisposable
{
    private readonly TerminalSuspendController _owner;
    private readonly IDisposable? _inputScope;
    private int _completed;

    internal TerminalSuspendScope(TerminalSuspendController owner, IDisposable? inputScope)
    {
        _owner = owner;
        _inputScope = inputScope;
        try { AppDomain.CurrentDomain.ProcessExit += OnProcessExit; } catch { /* ignore */ }
        try { Console.CancelKeyPress += OnCancelKeyPress; } catch { /* ignore */ }
    }

    /// <summary>True once the terminal has been given back (normally or via <see cref="Abandon"/>).</summary>
    public bool Completed => Volatile.Read(ref _completed) != 0;

    /// <summary>True once a Ctrl+C arrived while this hand-off was in flight.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>
    /// Absorb a Ctrl+C that arrived while the editor owned the terminal. Returns true when the
    /// default process termination should be cancelled, i.e. while the hand-off is still in
    /// flight: the SIGINT was delivered to the editor too, and killing Andy would strand the
    /// terminal. The normal restore still runs once the editor exits.
    /// </summary>
    public bool SuppressCancel()
    {
        CancelRequested = true;
        return !Completed;
    }

    /// <summary>Normal restore: re-enter the TUI, resume input, request a repaint.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        Unhook();
        _owner.Restore(_inputScope);
    }

    /// <summary>
    /// Emergency restore for Ctrl+C / process exit while the editor owns the terminal.
    /// Leaves the terminal outside the alternate screen with a visible cursor.
    /// </summary>
    public void Abandon()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        Unhook();
        _owner.Abandon(_inputScope);
    }

    private void Unhook()
    {
        try { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; } catch { /* ignore */ }
        try { Console.CancelKeyPress -= OnCancelKeyPress; } catch { /* ignore */ }
    }

    private void OnProcessExit(object? sender, EventArgs e) => Abandon();

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (SuppressCancel()) e.Cancel = true;
    }
}
