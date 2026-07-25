using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services.ToolResults;

/// <summary>One file's changes, recovered from a git_diff result.</summary>
/// <param name="Path">Path of the changed file.</param>
/// <param name="Added">Lines added, as reported by the tool.</param>
/// <param name="Removed">Lines removed, as reported by the tool.</param>
/// <param name="Diff">The change lines, in a form the diff renderer can draw.</param>
public sealed record GitFileDiff(string Path, int Added, int Removed, FileDiff Diff);

/// <summary>
/// Recovers per-file structure from a git_diff result (issue #257).
///
/// THIS CLASS SHOULD NOT NEED TO EXIST, and is written so it can be deleted.
///
/// Andy.Tools' GitDiffTool parses git's output into per-file hunks and then throws that structure
/// away: GitDiffFormatter renders it to markdown prose with emoji headings, truncates it at its
/// own line cap, and the tool returns the resulting STRING as its entire payload. So the client
/// has no structured result to read, and recovering one means reading back a rendering - exactly
/// the round trip the rest of this feed avoids.
///
/// The upstream fix is small, because the structure already exists inside the tool: return the
/// parsed file diffs as Data (and the counts as Metadata), keeping the formatted text as the
/// message for the model. Until that ships, this reader adapts what the tool does return, in one
/// place, against its documented output shape:
///
///     [emoji] **path/to/File.cs** (7 modifications)
///        **+5** additions, **-2** deletions
///       Lines 10-25:
///     ```diff
///     +   12: added content
///     -   13: removed content
///           14: context
///     ```
///
/// Anything it cannot recognize is left alone for the caller to render as plain output, so a
/// change in the upstream formatter degrades the display rather than corrupting it.
/// </summary>
public static class GitDiffOutputReader
{
    // "📄 **path** (7 modifications)" - the emoji is not matched, only the bold path.
    private static readonly Regex FileHeader = new(
        @"^\s*\W*\s*\*\*(?<path>[^*]+)\*\*\s*\((?<count>\d+)\s+modifications?\)",
        RegexOptions.Compiled);

    // "**+5** additions, **-2** deletions"
    private static readonly Regex Counts = new(
        @"\*\*\+(?<added>\d+)\*\*\s*additions?(?:.*?\*\*-(?<removed>\d+)\*\*\s*deletions?)?",
        RegexOptions.Compiled);

    // "+   12: content" / "-   13: content" / "     14: content"
    private static readonly Regex DiffLinePattern = new(
        @"^(?<sign>[+\- ])\s*(?<line>\d+):\s?(?<text>.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Read the per-file diffs out of a git_diff payload. Returns an empty list when the payload
    /// is not in the shape this reader knows, which the caller should treat as "render as text".
    /// </summary>
    public static IReadOnlyList<GitFileDiff> Read(string? output)
    {
        var files = new List<GitFileDiff>();
        if (string.IsNullOrWhiteSpace(output)) return files;

        string? path = null;
        int added = 0, removed = 0;
        var lines = new List<DiffLine>();

        void Flush()
        {
            if (path is null) return;
            if (lines.Count > 0 || added > 0 || removed > 0)
                files.Add(new GitFileDiff(path, added, removed, new FileDiff(lines.ToList(), added, removed)));
            lines.Clear();
            added = removed = 0;
        }

        foreach (var raw in ToolData.SplitLines(output))
        {
            var header = FileHeader.Match(raw);
            if (header.Success)
            {
                Flush();
                path = header.Groups["path"].Value.Trim().Trim('`');
                continue;
            }

            if (path is null) continue;

            var counts = Counts.Match(raw);
            if (counts.Success)
            {
                added = ParseInt(counts.Groups["added"].Value);
                removed = ParseInt(counts.Groups["removed"].Value);
                continue;
            }

            // Fence markers and hunk captions carry no content of their own.
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;
            if (raw.TrimStart().StartsWith("Lines ", StringComparison.Ordinal) && raw.TrimEnd().EndsWith(":", StringComparison.Ordinal))
            {
                // A gap between hunks, so the renderer can show the discontinuity.
                if (lines.Count > 0) lines.Add(new DiffLine(DiffLineKind.Gap, null, null, string.Empty));
                continue;
            }

            var diffLine = DiffLinePattern.Match(raw);
            if (!diffLine.Success) continue;

            int number = ParseInt(diffLine.Groups["line"].Value);
            var text = diffLine.Groups["text"].Value;
            switch (diffLine.Groups["sign"].Value)
            {
                case "+":
                    lines.Add(new DiffLine(DiffLineKind.Added, null, number, text));
                    break;
                case "-":
                    lines.Add(new DiffLine(DiffLineKind.Removed, number, null, text));
                    break;
                default:
                    lines.Add(new DiffLine(DiffLineKind.Context, number, number, text));
                    break;
            }
        }

        Flush();

        // When the tool reported no explicit counts, derive them from the lines it did show.
        return files.Select(f => f.Added == 0 && f.Removed == 0
            ? f with
            {
                Added = f.Diff.Lines.Count(l => l.Kind == DiffLineKind.Added),
                Removed = f.Diff.Lines.Count(l => l.Kind == DiffLineKind.Removed)
            }
            : f).ToList();
    }

    private static int ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
