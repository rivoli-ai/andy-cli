using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Services;

/// <summary>
/// Kind of a line in a computed diff.
/// </summary>
public enum DiffLineKind
{
    /// <summary>Unchanged line shown for context.</summary>
    Context,

    /// <summary>Line present only in the new content.</summary>
    Added,

    /// <summary>Line present only in the old content.</summary>
    Removed,

    /// <summary>A gap marker between hunks (elided unchanged lines).</summary>
    Gap
}

/// <summary>
/// One rendered diff line: its kind, the 1-based line numbers in the old/new files (null when the
/// line does not exist on that side), and the text.
/// </summary>
public sealed class DiffLine
{
    public DiffLineKind Kind { get; }
    public int? OldLineNumber { get; }
    public int? NewLineNumber { get; }
    public string Text { get; }

    public DiffLine(DiffLineKind kind, int? oldLineNumber, int? newLineNumber, string text)
    {
        Kind = kind;
        OldLineNumber = oldLineNumber;
        NewLineNumber = newLineNumber;
        Text = text;
    }
}

/// <summary>
/// A computed line-level diff between two texts: the lines to render (with hunk gaps), and the
/// total added/removed counts.
/// </summary>
public sealed class FileDiff
{
    public IReadOnlyList<DiffLine> Lines { get; }
    public int AddedCount { get; }
    public int RemovedCount { get; }

    /// <summary>True when both sides were identical (no changes).</summary>
    public bool IsEmpty => AddedCount == 0 && RemovedCount == 0;

    public FileDiff(IReadOnlyList<DiffLine> lines, int addedCount, int removedCount)
    {
        Lines = lines;
        AddedCount = addedCount;
        RemovedCount = removedCount;
    }
}

/// <summary>
/// Computes a git-style unified line diff between two texts. Pure and deterministic so it can be
/// unit-tested without any UI. Used to render file write/update operations in the feed.
/// </summary>
public static class UnifiedDiff
{
    // Above this many lines on either side, the O(n*m) LCS table is too expensive; we degrade to a
    // whole-file replace (all old removed, all new added) rather than risk a huge allocation.
    private const int LcsLineCap = 4000;

    /// <summary>
    /// Computes the diff. <paramref name="contextLines"/> unchanged lines are kept around each change;
    /// longer unchanged runs are collapsed into a single <see cref="DiffLineKind.Gap"/> marker.
    /// </summary>
    public static FileDiff Compute(string? oldText, string? newText, int contextLines = 3)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var ops = (oldLines.Length > LcsLineCap || newLines.Length > LcsLineCap)
            ? WholeFileReplace(oldLines, newLines)
            : DiffLines(oldLines, newLines);

        return BuildHunks(ops, Math.Max(0, contextLines));
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        // Normalize newlines, then split. A trailing newline does not create a spurious empty line.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Split('\n');
    }

    // Internal edit-script op before hunking: a line that is unchanged, added, or removed.
    private readonly struct Op
    {
        public readonly DiffLineKind Kind;
        public readonly int? OldLine;
        public readonly int? NewLine;
        public readonly string Text;

        public Op(DiffLineKind kind, int? oldLine, int? newLine, string text)
        {
            Kind = kind;
            OldLine = oldLine;
            NewLine = newLine;
            Text = text;
        }
    }

    private static List<Op> WholeFileReplace(string[] oldLines, string[] newLines)
    {
        var ops = new List<Op>(oldLines.Length + newLines.Length);
        for (int i = 0; i < oldLines.Length; i++)
        {
            ops.Add(new Op(DiffLineKind.Removed, i + 1, null, oldLines[i]));
        }
        for (int j = 0; j < newLines.Length; j++)
        {
            ops.Add(new Op(DiffLineKind.Added, null, j + 1, newLines[j]));
        }
        return ops;
    }

    // Classic LCS dynamic-programming line diff, then backtrack into an edit script.
    private static List<Op> DiffLines(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var ops = new List<Op>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                ops.Add(new Op(DiffLineKind.Context, x + 1, y + 1, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                ops.Add(new Op(DiffLineKind.Removed, x + 1, null, a[x]));
                x++;
            }
            else
            {
                ops.Add(new Op(DiffLineKind.Added, null, y + 1, b[y]));
                y++;
            }
        }
        while (x < n)
        {
            ops.Add(new Op(DiffLineKind.Removed, x + 1, null, a[x]));
            x++;
        }
        while (y < m)
        {
            ops.Add(new Op(DiffLineKind.Added, null, y + 1, b[y]));
            y++;
        }

        return ops;
    }

    // Keep only changed lines plus up to contextLines unchanged neighbours; collapse longer
    // unchanged runs into a single Gap marker.
    private static FileDiff BuildHunks(List<Op> ops, int contextLines)
    {
        int added = ops.Count(o => o.Kind == DiffLineKind.Added);
        int removed = ops.Count(o => o.Kind == DiffLineKind.Removed);

        if (added == 0 && removed == 0)
        {
            return new FileDiff(Array.Empty<DiffLine>(), 0, 0);
        }

        // Mark which context lines to keep: those within contextLines of any change.
        var keep = new bool[ops.Count];
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == DiffLineKind.Added || ops[i].Kind == DiffLineKind.Removed)
            {
                int lo = Math.Max(0, i - contextLines);
                int hi = Math.Min(ops.Count - 1, i + contextLines);
                for (int k = lo; k <= hi; k++)
                {
                    keep[k] = true;
                }
            }
        }

        var lines = new List<DiffLine>();
        bool inGap = false;
        for (int i = 0; i < ops.Count; i++)
        {
            if (keep[i])
            {
                var op = ops[i];
                lines.Add(new DiffLine(op.Kind, op.OldLine, op.NewLine, op.Text));
                inGap = false;
            }
            else if (!inGap)
            {
                // Start of an elided run.
                lines.Add(new DiffLine(DiffLineKind.Gap, null, null, ""));
                inGap = true;
            }
        }

        return new FileDiff(lines, added, removed);
    }
}
