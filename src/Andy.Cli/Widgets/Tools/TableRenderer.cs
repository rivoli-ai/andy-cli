using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Draws a bounded, column-aligned table as styled rows (issue #261).
    ///
    /// Tabular results - a dataframe preview, a schema, a process list - are the one case where a
    /// wrapped line is worse than a narrower column: alignment is what makes a table readable. So
    /// columns are sized to the content, numeric columns are right-aligned, and columns that do
    /// not fit are dropped from the right with a note, rather than wrapped.
    /// </summary>
    public static class TableRenderer
    {
        private const int ColumnGap = 2;
        private const int MinColumnWidth = 3;

        /// <summary>
        /// Render a table.
        /// </summary>
        /// <param name="headers">Column names.</param>
        /// <param name="rows">Row values, already stringified.</param>
        /// <param name="width">Columns available.</param>
        /// <param name="maxRows">Row budget including the header row.</param>
        /// <param name="theme">Active theme.</param>
        public static IReadOnlyList<StyledLine> Render(
            IReadOnlyList<string> headers,
            IReadOnlyList<IReadOnlyList<string>> rows,
            int width,
            int maxRows,
            Themes.Theme? theme = null)
        {
            theme ??= Themes.Theme.Current;
            if (headers.Count == 0 || width <= 0 || maxRows <= 1) return Array.Empty<StyledLine>();

            // One row of the budget goes to the header; the rest to data.
            int dataBudget = Math.Max(1, maxRows - 1);
            bool truncated = rows.Count > dataBudget;
            var shown = rows.Take(truncated ? Math.Max(1, dataBudget - 1) : dataBudget).ToList();

            var widths = ComputeWidths(headers, shown, width, out int visibleColumns);
            if (visibleColumns == 0) return Array.Empty<StyledLine>();

            var result = new List<StyledLine>
            {
                BuildRow(headers, widths, visibleColumns, theme.Accent, DL.CellAttrFlags.Bold, headers, theme)
            };

            foreach (var row in shown)
                result.Add(BuildRow(row, widths, visibleColumns, theme.ToolResult, DL.CellAttrFlags.None, headers, theme));

            if (visibleColumns < headers.Count)
                result[0] = result[0].WithSuffix(new StyledSpan(
                    $"  (+{headers.Count - visibleColumns} more columns)", theme.Ghost, DL.CellAttrFlags.Italic));

            if (truncated)
                result.Add(ToolOutputFormatter.OmissionMarker(rows.Count - shown.Count, theme));

            return result;
        }

        // Columns are sized to their widest value, then dropped from the right until the table
        // fits. Squeezing every column to fit would make all of them unreadable instead of some.
        private static int[] ComputeWidths(
            IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, int width, out int visibleColumns)
        {
            var widths = new int[headers.Count];
            for (int c = 0; c < headers.Count; c++)
            {
                int max = headers[c].Length;
                foreach (var row in rows)
                {
                    if (c < row.Count && row[c].Length > max) max = row[c].Length;
                }
                widths[c] = Math.Max(MinColumnWidth, max);
            }

            visibleColumns = 0;
            int used = 0;
            for (int c = 0; c < headers.Count; c++)
            {
                int needed = widths[c] + (c > 0 ? ColumnGap : 0);
                if (used + needed > width) break;
                used += needed;
                visibleColumns++;
            }

            // Always show at least one column, clipped to the available width if need be.
            if (visibleColumns == 0 && headers.Count > 0)
            {
                widths[0] = Math.Max(MinColumnWidth, Math.Min(widths[0], width));
                visibleColumns = 1;
            }

            return widths;
        }

        private static StyledLine BuildRow(
            IReadOnlyList<string> values, int[] widths, int visibleColumns,
            DL.Rgb24 color, DL.CellAttrFlags attributes, IReadOnlyList<string> headers, Themes.Theme theme)
        {
            var spans = new List<StyledSpan>();
            for (int c = 0; c < visibleColumns; c++)
            {
                var text = c < values.Count ? values[c] : string.Empty;
                if (text.Length > widths[c]) text = widths[c] > 3
                    ? text.Substring(0, widths[c] - 3) + "..."
                    : text.Substring(0, widths[c]);

                // Numbers right-align so their magnitudes line up and can be compared at a glance.
                var cell = LooksNumeric(text) ? text.PadLeft(widths[c]) : text.PadRight(widths[c]);
                if (c > 0) spans.Add(StyledSpan.Plain(new string(' ', ColumnGap)));
                spans.Add(new StyledSpan(cell, color, attributes));
            }
            return new StyledLine(spans);
        }

        private static bool LooksNumeric(string text)
            => text.Length > 0
               && (char.IsDigit(text[0]) || text[0] == '-' || text[0] == '+')
               && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

        /// <summary>Render a value for a table cell: null becomes a visible marker, not blank.</summary>
        public static string Cell(object? value) => value switch
        {
            null => "-",
            bool b => b ? "true" : "false",
            double d => d.ToString("0.####", CultureInfo.InvariantCulture),
            float f => f.ToString("0.####", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.####", CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "-"
        };
    }
}
