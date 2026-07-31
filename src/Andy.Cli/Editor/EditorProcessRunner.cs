using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Editor;

/// <summary>Outcome of one editor invocation.</summary>
/// <param name="Started">False when the process could not be started at all.</param>
/// <param name="ExitCode">The child's exit code. On Unix a child killed by signal N reports 128+N.</param>
/// <param name="FailureMessage">Populated when <paramref name="Started"/> is false.</param>
public readonly record struct EditorProcessResult(bool Started, int ExitCode, string? FailureMessage)
{
    /// <summary>The editor saved and exited cleanly.</summary>
    public bool Succeeded => Started && ExitCode == 0;

    /// <summary>Unix convention: exit codes above 128 mean the child died from signal (ExitCode - 128).</summary>
    public bool TerminatedBySignal => Started && ExitCode > 128 && ExitCode < 256;

    public static EditorProcessResult LaunchFailed(string message) => new(false, -1, message);
    public static EditorProcessResult Exited(int exitCode) => new(true, exitCode, null);
}

/// <summary>Launches the configured editor. Abstracted so tests can inject a deterministic stub.</summary>
public interface IEditorProcessRunner
{
    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="arguments"/> followed by
    /// <paramref name="filePath"/>, inheriting this process's stdin/stdout/stderr, and wait
    /// for it to exit.
    /// </summary>
    Task<EditorProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string filePath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default runner. Starts the editor with <c>UseShellExecute=false</c> and an explicit
/// <see cref="ProcessStartInfo.ArgumentList"/>, so NO shell is involved on any platform and
/// no argument is ever re-parsed, expanded or word-split. Standard streams are inherited so
/// a terminal editor draws straight onto the TTY the caller just handed over.
/// </summary>
public sealed class EditorProcessRunner : IEditorProcessRunner
{
    public async Task<EditorProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string filePath,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            // Inherit the terminal: the editor must own stdin/stdout while it runs.
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(filePath);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return EditorProcessResult.LaunchFailed(
                $"could not start \"{fileName}\": {ex.Message}. Check that it is installed and on PATH.");
        }
        catch (Exception ex)
        {
            return EditorProcessResult.LaunchFailed($"could not start \"{fileName}\": {ex.Message}");
        }

        if (process is null)
            return EditorProcessResult.LaunchFailed($"could not start \"{fileName}\": no process was created.");

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw;
            }

            return EditorProcessResult.Exited(process.ExitCode);
        }
    }
}
