using System;
using System.Threading;

namespace Andy.Cli.Input;

/// <summary>
/// Routes Ctrl+C (SIGINT) to whoever currently owns "interruptible" work, so the press cancels
/// that work instead of killing andy-cli.
///
/// Why this exists (issue #286): the interactive TUI deliberately leaves <c>isig</c> enabled on
/// the tty (<see cref="RawTerminalInput"/>), so Ctrl+C never arrives as a byte on stdin - the
/// terminal raises SIGINT and .NET surfaces it as <see cref="Console.CancelKeyPress"/>. The default
/// behaviour is to terminate the process, and <see cref="RawTerminalInput"/>'s own handler restores
/// the terminal on the way out. Neither is what you want while a user-invoked shell command is
/// running: the command should die, the TUI should not, and the terminal must stay in raw mode or
/// the next frame paints into a cooked terminal and the display is corrupted.
///
/// A handler installed here is consulted first. When it reports that it consumed the press, the
/// terminal is left untouched and <c>ConsoleCancelEventArgs.Cancel</c> is set so the runtime does
/// not terminate. When nothing is armed - the common case, an idle prompt - Ctrl+C behaves exactly
/// as it always has.
///
/// Handlers run on the runtime's SIGINT thread, so they must be cheap and non-blocking: cancel a
/// token and return.
/// </summary>
public sealed class InterruptDispatcher
{
    /// <summary>Process-wide dispatcher shared by the terminal input layer and the interactive loop.</summary>
    public static InterruptDispatcher Instance { get; } = new();

    private readonly object _sync = new();
    private Func<bool>? _handler;

    /// <summary>True when some component has claimed Ctrl+C.</summary>
    public bool IsArmed
    {
        get { lock (_sync) return _handler is not null; }
    }

    /// <summary>
    /// Claims Ctrl+C until the returned handle is disposed. <paramref name="handler"/> returns true
    /// when it consumed the press. Installing while another handler is armed replaces it; disposing
    /// restores the previous one, so nesting (a shell command started from inside another modal)
    /// cannot strand the dispatcher in a claimed state.
    /// </summary>
    public IDisposable Install(Func<bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Func<bool>? previous;
        lock (_sync)
        {
            previous = _handler;
            _handler = handler;
        }
        return new Registration(this, previous);
    }

    /// <summary>
    /// Offers a Ctrl+C press to the armed handler. Returns true when it was consumed, meaning the
    /// caller must suppress process termination and must NOT tear down the terminal.
    /// A handler that throws is treated as not having consumed the press, so a bug in a handler
    /// degrades to the ordinary Ctrl+C behaviour rather than making the app unkillable.
    /// </summary>
    public bool Dispatch()
    {
        Func<bool>? handler;
        lock (_sync) handler = _handler;
        if (handler is null) return false;

        try
        {
            return handler();
        }
        catch
        {
            return false;
        }
    }

    private void Restore(Func<bool>? previous)
    {
        lock (_sync) _handler = previous;
    }

    private sealed class Registration : IDisposable
    {
        private readonly InterruptDispatcher _owner;
        private readonly Func<bool>? _previous;
        private int _disposed;

        public Registration(InterruptDispatcher owner, Func<bool>? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Restore(_previous);
        }
    }
}
