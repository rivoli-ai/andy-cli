using System;
using System.IO;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// The identity of the file Andy just wrote, captured before any formatter runs, so a formatter
/// that deletes the target or swaps it for a link somewhere else is detected rather than silently
/// tolerated.
/// </summary>
/// <param name="Path">The absolute path Andy mutated.</param>
/// <param name="Existed">Whether a regular file was present at <paramref name="Path"/>.</param>
/// <param name="WasLink">Whether the path was already a symbolic link before formatting.</param>
/// <param name="LinkTarget">The link's final target when it was a link.</param>
public sealed record FormatterTargetIdentity(string Path, bool Existed, bool WasLink, string? LinkTarget)
{
    /// <summary>Capture the identity of a target path.</summary>
    public static FormatterTargetIdentity Capture(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new FormatterTargetIdentity(path, false, false, null);
            }

            var link = info.ResolveLinkTarget(returnFinalTarget: true);
            return new FormatterTargetIdentity(path, true, link is not null, link?.FullName);
        }
        catch (Exception)
        {
            // A probe failure must not be read as "gone"; treat identity as unknown-but-present.
            return new FormatterTargetIdentity(path, File.Exists(path), false, null);
        }
    }
}

/// <summary>
/// Post-run safety check for the target file.
///
/// A formatter is an arbitrary local binary. A buggy or hostile one can delete the file, replace it
/// with a symlink pointing elsewhere, or leave a directory in its place. Any of those would make
/// the diff Andy shows a fiction, and continuing to run further formatters would operate on
/// something that is no longer the file that was written. Every such case is reported as a failure
/// and stops the remaining formatters for that file.
/// </summary>
public static class FormatterTargetGuard
{
    /// <summary>
    /// Verify the target still is the file that was written. Returns null when everything is fine,
    /// or the outcome (with a reason) that must be recorded.
    /// </summary>
    public static (FormatterOutcome Outcome, string Reason)? Check(FormatterTargetIdentity before)
    {
        try
        {
            if (Directory.Exists(before.Path))
            {
                return (FormatterOutcome.TargetEscaped,
                    "the formatter replaced the target file with a directory");
            }

            if (!File.Exists(before.Path))
            {
                return (FormatterOutcome.TargetMissing,
                    "the formatter removed the target file");
            }

            var info = new FileInfo(before.Path);
            var link = info.ResolveLinkTarget(returnFinalTarget: true);

            if (link is not null && !before.WasLink)
            {
                return (FormatterOutcome.TargetEscaped,
                    $"the formatter replaced the target file with a link to {link.FullName}");
            }

            if (link is not null && before.WasLink
                && !string.Equals(link.FullName, before.LinkTarget, StringComparison.Ordinal))
            {
                return (FormatterOutcome.TargetEscaped,
                    $"the formatter repointed the target link to {link.FullName}");
            }

            return null;
        }
        catch (Exception ex)
        {
            return (FormatterOutcome.TargetEscaped,
                $"could not verify the target file after formatting: {ex.Message}");
        }
    }
}
