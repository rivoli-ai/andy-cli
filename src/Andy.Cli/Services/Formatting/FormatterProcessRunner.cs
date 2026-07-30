using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Services.Formatting;

/// <summary>One formatter process invocation.</summary>
/// <param name="Command">Executable to run (already resolved, or resolvable by the OS).</param>
/// <param name="Arguments">Argument vector; passed through <see cref="ProcessStartInfo.ArgumentList"/>, never a shell.</param>
/// <param name="WorkingDirectory">Directory the process runs in.</param>
/// <param name="Timeout">Wall-clock limit; the process is killed when it elapses.</param>
public sealed record FormatterProcessRequest(
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout);

/// <summary>The observable outcome of a formatter process.</summary>
/// <param name="Started">False when the process could not be launched at all (missing binary).</param>
/// <param name="ExitCode">Exit code when the process ran to completion.</param>
/// <param name="StandardOutput">Captured stdout, already bounded.</param>
/// <param name="StandardError">Captured stderr, already bounded.</param>
/// <param name="TimedOut">True when the timeout elapsed and the process was killed.</param>
/// <param name="Cancelled">True when the caller's cancellation token fired and the process was killed.</param>
/// <param name="StartFailure">Why the launch failed, when <paramref name="Started"/> is false.</param>
public sealed record FormatterProcessResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    string? StartFailure)
{
    public static FormatterProcessResult NotStarted(string reason)
        => new(false, -1, string.Empty, string.Empty, false, false, reason);
}

/// <summary>
/// Runs a formatter process. Abstracted so the post-mutation pipeline can be unit-tested without
/// launching real binaries - which is what makes timeout, cancellation, nonzero-exit, and
/// target-escape scenarios testable deterministically.
/// </summary>
public interface IFormatterProcessRunner
{
    Task<FormatterProcessResult> RunAsync(FormatterProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The real runner: launches the formatter with no shell, captures a bounded amount of stdout and
/// stderr, kills the whole process tree on timeout or cancellation, and never throws for a
/// formatter that simply fails.
/// </summary>
public sealed class FormatterProcessRunner : IFormatterProcessRunner
{
    /// <summary>
    /// Hard cap on captured output per stream. A formatter that floods stdout must not be able to
    /// grow Andy's memory or its context; the excess is dropped as it arrives, not afterwards.
    /// </summary>
    public const int MaxCapturedCharsPerStream = 16 * 1024;

    public async Task<FormatterProcessResult> RunAsync(
        FormatterProcessRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Command,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdout = new BoundedBuffer(MaxCapturedCharsPerStream);
        var stderr = new BoundedBuffer(MaxCapturedCharsPerStream);
        process.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);

        try
        {
            if (!process.Start())
            {
                return FormatterProcessResult.NotStarted($"could not start '{request.Command}'");
            }
        }
        catch (Exception ex)
        {
            // A missing binary surfaces here (Win32Exception / ComponentModel). Reported, never thrown.
            return FormatterProcessResult.NotStarted($"could not start '{request.Command}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);

        bool timedOut = false;
        bool cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested;
            cancelled = !timedOut;
            KillQuietly(process);
        }

        // Flush the async readers so whatever the process managed to emit is reported even when it
        // was killed; a killed formatter's stderr is usually the most useful part of the report.
        try
        {
            process.WaitForExit(1000);
        }
        catch (Exception)
        {
            // Nothing more to do; the buffers hold whatever arrived.
        }

        var exitCode = -1;
        if (!timedOut && !cancelled)
        {
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exitCode = -1;
            }
        }

        return new FormatterProcessResult(
            Started: true,
            ExitCode: exitCode,
            StandardOutput: stdout.ToString(),
            StandardError: stderr.ToString(),
            TimedOut: timedOut,
            Cancelled: cancelled,
            StartFailure: null);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill.
        }
    }

    /// <summary>Append-only buffer that stops growing at a fixed cap. Thread-safe for the two reader callbacks.</summary>
    private sealed class BoundedBuffer
    {
        private readonly StringBuilder _builder = new();
        private readonly int _limit;
        private readonly object _gate = new();
        private bool _truncated;

        public BoundedBuffer(int limit) => _limit = limit;

        public void AppendLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_builder.Length >= _limit)
                {
                    _truncated = true;
                    return;
                }

                var remaining = _limit - _builder.Length;
                if (line.Length + 1 > remaining)
                {
                    _builder.Append(line, 0, Math.Max(0, remaining - 1));
                    _truncated = true;
                    return;
                }

                _builder.Append(line).Append('\n');
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                var text = _builder.ToString().TrimEnd('\n');
                return _truncated ? text + "\n[formatter output truncated]" : text;
            }
        }
    }
}
