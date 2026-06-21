using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Andy.Cli.Input;

/// <summary>
/// Reads raw bytes from the controlling terminal so the CLI can receive mouse
/// wheel events (which <c>Console.ReadKey</c> cannot deliver) while keeping all
/// existing keyboard handling.
///
/// On start it puts the TTY into cbreak mode via <c>stty</c> (no echo, no
/// canonical line buffering, CR left untranslated) and spins a background thread
/// that polls fd 0 and feeds bytes through <see cref="TerminalInputParser"/>.
/// Decoded events are queued for the main loop to drain. SGR mouse reporting is
/// off by default (so the terminal's native click-drag text selection keeps
/// working) and can be toggled at runtime via <see cref="SetMouseReporting"/>.
/// The original terminal settings and mouse mode are restored on
/// <see cref="Dispose"/>, process exit, and Ctrl+C.
///
/// Input is read with libc <c>poll</c>/<c>read</c> on fd 0 rather than
/// <c>Console.OpenStandardInput()</c>: the .NET console stream forces canonical
/// (line-buffered) reads that ignore our cbreak settings, whereas the raw
/// syscalls honor them and let a poll timeout drive the lone-ESC flush.
///
/// If the input is redirected or <c>stty</c> is unavailable,
/// <see cref="TryStart"/> returns <c>null</c> and the caller falls back to the
/// legacy <c>Console.ReadKey</c> loop.
/// </summary>
public sealed class RawTerminalInput : IDisposable
{
    private const int StdinFd = 0;
    private const short POLLIN = 0x0001;
    private const short POLLERR = 0x0008;
    private const short POLLHUP = 0x0010;
    private const short POLLNVAL = 0x0020;
    private const int PollTimeoutMs = 150;

    private readonly TerminalInputParser _parser = new();
    private readonly ConcurrentQueue<TerminalInputEvent> _queue = new();
    private readonly Thread _thread;
    private readonly string _savedStty;
    private readonly MouseReporting _mouse;
    private volatile bool _stop;
    private int _restored; // 0 = not yet restored, 1 = restored (guards idempotency)

    private RawTerminalInput(string savedStty, bool enableMouse)
    {
        _savedStty = savedStty;
        _mouse = new MouseReporting(Console.Write);
        if (enableMouse) _mouse.Set(true);

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;

        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "tty-raw-input" };
        _thread.Start();
    }

    /// <summary>
    /// Whether SGR mouse reporting is currently enabled. When on, the terminal
    /// forwards mouse events to the app and native click-drag text selection is
    /// suppressed.
    /// </summary>
    public bool MouseEnabled => _mouse.Enabled;

    /// <summary>
    /// Turn SGR mouse reporting on or off at runtime and return the new state.
    /// </summary>
    public bool SetMouseReporting(bool enabled) => _mouse.Set(enabled);

    /// <summary>Flip mouse reporting and return the new state.</summary>
    public bool ToggleMouseReporting() => _mouse.Toggle();

    /// <summary>
    /// Attempt to switch the terminal into raw byte mode. Returns <c>null</c>
    /// when input is not an interactive TTY or <c>stty</c> is unavailable, in
    /// which case the caller should keep using <c>Console.ReadKey</c>.
    ///
    /// <paramref name="enableMouse"/> defaults to <c>true</c> so the mouse wheel
    /// scrolls the feed out of the box. To select text while capture is on, hold
    /// Option (macOS) / Shift (xterm) and drag - terminals route that around mouse
    /// reporting. Callers can flip capture off (restoring plain click-drag native
    /// selection) up front or at runtime via <see cref="SetMouseReporting"/> (F3).
    /// </summary>
    public static RawTerminalInput? TryStart(bool enableMouse = true)
    {
        if (Console.IsInputRedirected) return null;

        if (!RunStty("-g", out string saved) || string.IsNullOrWhiteSpace(saved))
            return null;
        saved = saved.Trim();

        // cbreak: no echo, no canonical buffering, leave CR untranslated so
        // Enter stays 0x0D (submit) and Ctrl+Enter/LF stays 0x0A (newline).
        // min 0 / time 1 lets reads return promptly. isig stays on so Ctrl+C
        // still works.
        if (!RunStty("-echo -icanon -icrnl min 0 time 1", out _))
        {
            // Best effort to undo anything partially applied, then bail.
            RunStty(saved, out _);
            return null;
        }

        try
        {
            return new RawTerminalInput(saved, enableMouse);
        }
        catch
        {
            RunStty(saved, out _);
            return null;
        }
    }

    /// <summary>Dequeue the next decoded input event, if any.</summary>
    public bool TryDequeue(out TerminalInputEvent ev) => _queue.TryDequeue(out ev);

    private void ReadLoop()
    {
        var fds = new PollFd[1];
        var buf = new byte[1024];
        while (!_stop)
        {
            fds[0] = new PollFd { fd = StdinFd, events = POLLIN, revents = 0 };

            int pr;
            try { pr = poll(fds, (nuint)1, PollTimeoutMs); }
            catch { break; }

            if (_stop) break;
            if (pr < 0) continue; // interrupted (EINTR) etc.

            if (pr == 0)
            {
                // Idle tick: resolve a pending lone ESC into an Escape key.
                foreach (var ev in _parser.Flush()) _queue.Enqueue(ev);
                continue;
            }

            short revents = fds[0].revents;
            if ((revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) break; // terminal closed

            if ((revents & POLLIN) != 0)
            {
                nint n;
                try { n = read(StdinFd, buf, (nuint)buf.Length); }
                catch { break; }

                if (n <= 0) { if (n == 0) break; else continue; }
                foreach (var ev in _parser.Feed(buf, (int)n)) _queue.Enqueue(ev);
            }
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => RestoreTerminal();

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) => RestoreTerminal();

    private void RestoreTerminal()
    {
        // Run at most once across Dispose / ProcessExit / Ctrl+C.
        if (Interlocked.Exchange(ref _restored, 1) != 0) return;
        // Always disable mouse reporting on the way out if it was ever enabled,
        // so the terminal is left with native click-drag selection restored.
        SetMouseReporting(false);
        try { RunStty(_savedStty, out _); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        _stop = true;
        try { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; } catch { /* ignore */ }
        try { Console.CancelKeyPress -= OnCancelKeyPress; } catch { /* ignore */ }
        RestoreTerminal();
        try { _thread.Join(300); } catch { /* ignore */ }
    }

    private static bool RunStty(string args, out string stdout)
    {
        stdout = string.Empty;
        try
        {
            var psi = new ProcessStartInfo("stty", args)
            {
                // Inherit our controlling TTY as stty's stdin so it can read and
                // change the real terminal settings.
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            stdout = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(2000)) return false;
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ----- libc interop (poll/read honor our cbreak termios; the .NET console
    // stream does not) -----

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nuint count);
}
