using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// Where a formatter definition came from. Also the merge precedence: a project definition
/// overrides a user definition of the same name, which overrides a locally detected builtin.
/// </summary>
public enum FormatterSource
{
    /// <summary>A well-known formatter recognised only when its binary is already on PATH.</summary>
    Detected,

    /// <summary>Defined in the user-level formatter config.</summary>
    User,

    /// <summary>Defined in the project-level formatter config.</summary>
    Project,
}

/// <summary>
/// One configured (or locally detected) formatter: the command to run, its arguments, the file
/// extensions it applies to, where to run it, how long to let it run, and whether it is enabled.
///
/// Andy never installs a formatter. A definition only ever runs when its command already resolves
/// on the machine (see <see cref="FormatterAvailability"/>).
/// </summary>
public sealed record FormatterDefinition
{
    /// <summary>Placeholder replaced with the absolute path of the file being formatted.</summary>
    public const string FilePlaceholder = "$FILE";

    /// <summary>Stable identifier, unique within the merged set. Used for ordering ties and reporting.</summary>
    public required string Name { get; init; }

    /// <summary>The executable to run. Never installed; it must already resolve locally.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// Arguments passed to <see cref="Command"/>. Any argument equal to or containing
    /// <see cref="FilePlaceholder"/> has the placeholder replaced with the target file's absolute
    /// path. When no argument mentions the placeholder, the path is appended as the last argument.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// File extensions this formatter handles, with or without a leading dot. Matching is
    /// case-insensitive. An empty list never matches (a formatter with no extensions is inert
    /// rather than universal, so a typo cannot make one formatter run on every file).
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Working directory for the process. Relative paths resolve against the session working
    /// directory. Null runs the formatter in the session working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Wall-clock limit for one run. Clamped to [1, 600] seconds.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Whether this formatter may run at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Sort key for deterministic ordering when several formatters match the same file. Lower runs
    /// first; ties break on <see cref="Name"/> using ordinal comparison, so the order never depends
    /// on dictionary or filesystem enumeration order.
    /// </summary>
    public int Order { get; init; } = 100;

    /// <summary>Which config layer produced this definition.</summary>
    public FormatterSource Source { get; init; } = FormatterSource.Project;

    /// <summary>The timeout as a <see cref="TimeSpan"/>, clamped to a sane range.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds <= 0 ? 30 : TimeoutSeconds, 1, 600));

    /// <summary>True when this definition declares the target file's extension.</summary>
    public bool MatchesExtension(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || Extensions.Count == 0)
        {
            return false;
        }

        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        foreach (var declared in Extensions)
        {
            if (string.IsNullOrWhiteSpace(declared))
            {
                continue;
            }

            var normalized = declared.StartsWith('.') ? declared : "." + declared;
            if (string.Equals(normalized, ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The concrete argument vector for one file: <see cref="FilePlaceholder"/> substituted, or the
    /// path appended when the definition never mentions it.
    /// </summary>
    public IReadOnlyList<string> ResolveArguments(string absoluteFilePath)
    {
        var mentioned = Arguments.Any(a => a is not null && a.Contains(FilePlaceholder, StringComparison.Ordinal));
        var resolved = new List<string>(Arguments.Count + 1);
        foreach (var arg in Arguments)
        {
            resolved.Add(arg is null
                ? string.Empty
                : arg.Replace(FilePlaceholder, absoluteFilePath, StringComparison.Ordinal));
        }

        if (!mentioned)
        {
            resolved.Add(absoluteFilePath);
        }

        return resolved;
    }

    /// <summary>
    /// The command line as a single string, used for the permission specifier and for display.
    /// Arguments containing whitespace are quoted so the string round-trips readably; this is a
    /// display/matching form, not something handed to a shell.
    /// </summary>
    public string DescribeCommandLine(string absoluteFilePath)
    {
        var parts = new List<string> { Command };
        parts.AddRange(ResolveArguments(absoluteFilePath));
        return string.Join(' ', parts.Select(Quote));

        static string Quote(string value)
            => value.Length > 0 && !value.Any(char.IsWhiteSpace) ? value : "\"" + value + "\"";
    }
}
