using System;
using System.Collections.Generic;
using System.Linq;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>The display rows for a tool's output, plus what was left out.</summary>
    /// <param name="Rows">Rows ready to draw, already wrapped to the requested width.</param>
    /// <param name="OmittedRows">How many rows the middle marker stands in for; 0 when nothing was dropped.</param>
    /// <param name="TotalRows">How many rows the full output would have occupied.</param>
    public sealed record FormattedOutput(
        IReadOnlyList<StyledLine> Rows,
        int OmittedRows,
        int TotalRows)
    {
        /// <summary>Nothing to show.</summary>
        public static FormattedOutput Empty { get; } = new(Array.Empty<StyledLine>(), 0, 0);

        /// <summary>True when the output was trimmed to fit.</summary>
        public bool WasTruncated => OmittedRows > 0;
    }

    /// <summary>
    /// Turns raw tool output into a bounded block of display rows (issue #250).
    ///
    /// Three rules, all of which the previous implementation got wrong:
    ///
    /// 1. WRAP FIRST, THEN TRUNCATE, and budget in screen rows rather than logical lines. A result
    ///    of three 400-character lines used to pass a 5-line cap and then occupy 30 rows.
    /// 2. KEEP THE HEAD AND THE TAIL. Head-only truncation drops the end of a failing command's
    ///    output, which is exactly where the error is.
    /// 3. NEVER HARD-TRUNCATE A ROW. Collapsed mode used to cut each line at width-5 and append
    ///    "..."; wrapping keeps the text readable instead.
    /// </summary>
    public static class ToolOutputFormatter
    {
        /// <summary>Rows shown for a completed tool in collapsed mode.</summary>
        public const int CollapsedRowBudget = 6;

        /// <summary>Rows shown for a completed tool in expanded mode (ctrl+o).</summary>
        public const int ExpandedRowBudget = 40;

        /// <summary>
        /// Decode, wrap and bound <paramref name="text"/>.
        /// </summary>
        /// <param name="text">Raw tool output, possibly containing ANSI escapes.</param>
        /// <param name="width">Columns available for the output body.</param>
        /// <param name="maxRows">Row budget, including the row the omission marker occupies.</param>
        /// <param name="theme">Theme used to map ANSI colors; defaults to the current theme.</param>
        public static FormattedOutput Format(string? text, int width, int maxRows, Themes.Theme? theme = null)
        {
            if (string.IsNullOrEmpty(text) || width <= 0 || maxRows <= 0) return FormattedOutput.Empty;
            theme ??= Themes.Theme.Current;

            // Wrap first so the budget is measured in the rows the user will actually see.
            var rows = new List<StyledLine>();
            foreach (var line in AnsiText.DecodeLines(text, theme))
            {
                if (line.IsEmpty) { rows.Add(StyledLine.Empty); continue; }
                rows.AddRange(line.Wrap(width));
            }

            // Trailing blank rows are an artifact of the output's final newline, not content.
            while (rows.Count > 0 && rows[^1].IsEmpty) rows.RemoveAt(rows.Count - 1);
            if (rows.Count == 0) return FormattedOutput.Empty;
            if (rows.Count <= maxRows) return new FormattedOutput(rows, 0, rows.Count);

            // One row goes to the marker; split the rest between head and tail, favoring the head
            // slightly so the start of the output still reads as a beginning.
            int available = maxRows - 1;
            int tail = Math.Max(1, available / 2);
            int head = Math.Max(1, available - tail);
            int omitted = rows.Count - head - tail;
            if (omitted <= 0) return new FormattedOutput(rows.Take(maxRows).ToList(), 0, rows.Count);

            var kept = new List<StyledLine>(maxRows);
            kept.AddRange(rows.Take(head));
            kept.Add(OmissionMarker(omitted, theme));
            kept.AddRange(rows.Skip(rows.Count - tail));
            return new FormattedOutput(kept, omitted, rows.Count);
        }

        /// <summary>
        /// The row that stands in for the omitted middle. It states the count, because "..." alone
        /// reads as "a bit more" whether five or five thousand rows were dropped.
        /// </summary>
        public static StyledLine OmissionMarker(int omittedRows, Themes.Theme? theme = null)
        {
            theme ??= Themes.Theme.Current;
            var hint = ToolOutputView.Expanded ? string.Empty : " (ctrl+o to expand)";
            return StyledLine.Plain($"... +{omittedRows} lines{hint}", theme.Ghost, DL.CellAttrFlags.Italic);
        }

        /// <summary>
        /// The placeholder for a tool that succeeded without producing output. Rendering nothing
        /// leaves the user unable to tell "no output" from "output was lost".
        /// </summary>
        public static StyledLine NoOutputMarker(Themes.Theme? theme = null)
        {
            theme ??= Themes.Theme.Current;
            return StyledLine.Plain("(no output)", theme.Ghost, DL.CellAttrFlags.Italic);
        }

        /// <summary>The row budget for the current collapsed/expanded mode.</summary>
        public static int CurrentRowBudget => ToolOutputView.Expanded ? ExpandedRowBudget : CollapsedRowBudget;

        /// <summary>
        /// Format an elapsed duration compactly: sub-second in ms, then seconds, then minutes.
        /// </summary>
        public static string FormatDuration(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            if (elapsed.TotalMilliseconds < 1000) return $"{elapsed.TotalMilliseconds:F0}ms";
            if (elapsed.TotalSeconds < 60) return $"{elapsed.TotalSeconds:F1}s";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:D2}s";
            return $"{(int)elapsed.TotalHours}h{elapsed.Minutes:D2}m";
        }

        /// <summary>
        /// Durations below this are noise rather than information and are not shown on the header.
        /// </summary>
        public static readonly TimeSpan MinimumReportedDuration = TimeSpan.FromMilliseconds(200);

        /// <summary>Thousands-separated count, so "12,481 rows" does not read as "12481".</summary>
        public static string FormatCount(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>"1 match" / "2 matches" without the caller repeating the ternary each time.</summary>
        public static string Pluralize(long count, string singular, string? plural = null)
            => $"{FormatCount(count)} {(count == 1 ? singular : plural ?? singular + "s")}";
    }
}
