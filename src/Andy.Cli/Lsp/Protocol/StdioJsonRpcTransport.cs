using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Andy.Cli.Lsp.Protocol;

/// <summary>
/// Launches a language server as a child process and speaks the base protocol over its stdio.
///
/// Process hygiene is the whole point of this class:
/// - A missing binary surfaces as <see cref="LspStartupException"/> with the command that failed,
///   never as an unhandled Win32Exception escaping into the agent loop.
/// - stderr is drained continuously (a server that fills its stderr pipe would otherwise deadlock)
///   and the tail is kept for diagnostics.
/// - <see cref="DisposeAsync"/> always ends the process, killing the whole tree, so a session that
///   ends - cleanly or not - leaves no orphan servers behind.
/// </summary>
public sealed class StdioLspTransport : ILspTransport
{
    private const int StandardErrorTailLines = 20;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _stderr = new();
    private Task? _stderrDrain;
    private int _disposed;

    private StdioLspTransport(Process process, string description)
    {
        _process = process;
        Description = description;
    }

    public Stream Input => _process.StandardInput.BaseStream;

    public Stream Output => _process.StandardOutput.BaseStream;

    public string Description { get; }

    public int ProcessId { get; private set; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The last few stderr lines. When the process has already exited this waits briefly for the
    /// drain task to finish, because the interesting case - a server that printed its reason and
    /// died - is exactly the one where the read loop notices the closed stdout before the stderr
    /// lines have been consumed. The wait is bounded and only ever happens on a failure path.
    /// </summary>
    public string StandardErrorTail
    {
        get
        {
            try
            {
                if (HasExited)
                {
                    _stderrDrain?.Wait(TimeSpan.FromMilliseconds(500));
                }
            }
            catch
            {
                // Drain faulted or was already gone; report whatever was captured.
            }

            return string.Join("\n", _stderr);
        }
    }

    public static ILspTransport Start(LspServerDefinition definition, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = definition.Command,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = null,
        };

        foreach (var argument in definition.Args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in definition.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var description = definition.Args.Count == 0
            ? definition.Command
            : definition.Command + " " + string.Join(" ", definition.Args);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new LspStartupException(
                    definition.Id,
                    $"Could not launch '{description}': the operating system returned no process.");
        }
        catch (LspStartupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LspStartupException(
                definition.Id,
                $"Could not launch '{description}': {ex.Message}. "
                + "Install the language server and make sure the command is on PATH, "
                + "or correct 'command' in .andy/lsp-servers.json. Andy never downloads language servers.",
                ex);
        }

        var transport = new StdioLspTransport(process, description) { ProcessId = SafeProcessId(process) };
        transport.StartDrainingStandardError();
        return transport;
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return 0;
        }
    }

    private void StartDrainingStandardError()
    {
        _stderrDrain = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    _stderr.Enqueue(line);
                    while (_stderr.Count > StandardErrorTailLines)
                    {
                        _stderr.TryDequeue(out _);
                    }
                }
            }
            catch
            {
                // The pipe closes when the server exits; nothing to report here.
            }
        });
    }

    public void Terminate()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone, or we lost the right to signal it. Either way there is nothing left to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            if (!_process.HasExited)
            {
                // Give a server that already received shutdown/exit a moment to leave on its own.
                using var grace = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try
                {
                    await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Terminate();
                }
            }
        }
        catch
        {
            Terminate();
        }

        try
        {
            _process.Dispose();
        }
        catch
        {
            // Nothing further to release.
        }
    }
}

/// <summary>A configured language server could not be started.</summary>
public sealed class LspStartupException : Exception
{
    public LspStartupException(string serverId, string message, Exception? inner = null)
        : base(message, inner) => ServerId = serverId;

    public string ServerId { get; }
}
