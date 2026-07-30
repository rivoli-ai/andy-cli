using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// Result of running an external credential helper. <see cref="StandardOutput"/> may contain
/// secret material, so callers must treat it as such; <see cref="StandardError"/> is scrubbed
/// by the caller before it reaches a message.
/// </summary>
internal sealed record SecretProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs an external credential helper (macOS <c>security</c>, Linux <c>secret-tool</c>) with the
/// secret supplied on <b>stdin only</b>.
///
/// SECURITY: process arguments are readable by other processes on macOS and Linux, so a secret
/// must never appear in <paramref name="arguments"/>. Every write path here pipes the value in
/// through stdin instead. Argument values are also passed as a list (never a joined string), so
/// no shell quoting is involved and no shell is spawned.
/// </summary>
internal static class SecretProcessRunner
{
    public static async Task<SecretProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdin,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new CredentialStoreException($"Could not start the credential helper '{fileName}'.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new CredentialStoreUnavailableException(
                $"The credential helper '{fileName}' is not available on this system.", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            if (stdin != null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (System.IO.IOException)
        {
            // The helper exited before reading all of stdin; the exit code below tells the story.
        }
        finally
        {
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new SecretProcessResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Whether an executable of the given name can be started at all. Used by the store
    /// implementations for their availability probe.
    /// </summary>
    public static bool CanRun(string fileName, IReadOnlyList<string> probeArguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in probeArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                TryKill(process);
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
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
            // Best effort only.
        }
    }

    /// <summary>Builds the stdin payload for a helper that prompts for the value twice.</summary>
    public static string RepeatedLine(string value, int times)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < times; i++)
        {
            builder.Append(value).Append('\n');
        }

        return builder.ToString();
    }
}
