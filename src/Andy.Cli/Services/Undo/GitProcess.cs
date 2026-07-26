using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Andy.Cli.Services.Undo;

/// <summary>
/// Raised when a shadow-snapshot operation cannot be completed safely. The undo
/// subsystem always refuses (throws) rather than applying a partial restore.
/// </summary>
public sealed class SnapshotException : Exception
{
    public SnapshotException(string message) : base(message) { }
    public SnapshotException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Outcome of a single git invocation (stdout captured as raw bytes so blobs survive).</summary>
internal sealed class GitResult
{
    public int ExitCode { get; init; }
    public byte[] Output { get; init; } = Array.Empty<byte>();
    public string Error { get; init; } = string.Empty;
    public bool Success => ExitCode == 0;
    public string Text => Encoding.UTF8.GetString(Output);
}

/// <summary>
/// Minimal git process runner used by the shadow snapshot store. Every call gets
/// an explicit environment so git can never fall back to the user's repository,
/// index, or configuration.
/// </summary>
internal static class GitProcess
{
    public const int DefaultTimeoutMs = 120_000;

    public static GitResult Run(
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<string> arguments,
        int timeoutMs = DefaultTimeoutMs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in environment)
        {
            if (pair.Value is null)
            {
                startInfo.Environment.Remove(pair.Key);
            }
            else
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new SnapshotException("Unable to start git.");
        }
        catch (Win32Exception ex)
        {
            throw new SnapshotException("git executable was not found on PATH.", ex);
        }

        using (process)
        {
            using var stdout = new MemoryStream();
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // The process may have exited between the wait and the kill.
                }
                throw new SnapshotException(
                    $"git {string.Join(' ', arguments)} timed out after {timeoutMs} ms.");
            }

            copyTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();

            return new GitResult
            {
                ExitCode = process.ExitCode,
                Output = stdout.ToArray(),
                Error = error
            };
        }
    }
}
