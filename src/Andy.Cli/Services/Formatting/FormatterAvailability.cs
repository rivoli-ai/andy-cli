using System;
using System.Collections.Generic;
using System.IO;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// Resolves a formatter command to an executable that already exists on this machine.
///
/// Andy never installs a formatter: a definition whose command does not resolve is reported as
/// unavailable and skipped, and the reason is surfaced by <c>/formatters status</c>. Resolution is
/// PATH-based (plus PATHEXT on Windows), or a direct filesystem probe when the command is already
/// a path.
/// </summary>
public static class FormatterAvailability
{
    /// <summary>
    /// The absolute path of the executable backing <paramref name="command"/>, or null when nothing
    /// on this machine provides it. Never installs, downloads, or otherwise acquires anything.
    /// </summary>
    public static string? Resolve(string? command)
        => Resolve(command, Environment.GetEnvironmentVariable, File.Exists);

    /// <summary>
    /// Testable core of <see cref="Resolve(string?)"/>: environment and filesystem access are
    /// injected so tests can describe a machine without touching the real one.
    /// </summary>
    internal static string? Resolve(
        string? command,
        Func<string, string?> getEnvironmentVariable,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var candidateExtensions = ExecutableExtensions(getEnvironmentVariable);

        // A path-qualified command is probed directly; PATH is not consulted for it.
        if (command.Contains('/') || command.Contains('\\'))
        {
            foreach (var suffix in candidateExtensions)
            {
                var candidate = command + suffix;
                if (fileExists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        var pathValue = getEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var suffix in candidateExtensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim(), command + suffix);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry must not break formatter resolution.
                    continue;
                }

                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ExecutableExtensions(Func<string, string?> getEnvironmentVariable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new[] { string.Empty };
        }

        var pathExt = getEnvironmentVariable("PATHEXT");
        var list = new List<string> { string.Empty };
        if (!string.IsNullOrEmpty(pathExt))
        {
            foreach (var ext in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                list.Add(ext.StartsWith('.') ? ext : "." + ext);
            }
        }

        return list;
    }
}
