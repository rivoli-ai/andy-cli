using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Andy.Cli.Services;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Renders computed diffs and file contents as styled rows (issues #253, #254).
    ///
    /// Content is syntax-highlighted by file extension, which the existing diff widget never did -
    /// <see cref="CodeHighlighter"/> was wired only to markdown fenced code blocks. Because
    /// highlighted content no longer reads as "all green" or "all red", the added/removed
    /// distinction is carried by a row tint and the sign column instead of by the text color.
    /// </summary>
    public static class DiffRenderer
    {
        /// <summary>Minimum width of the line-number gutter.</summary>
        private const int MinGutterWidth = 3;

        /// <summary>
        /// Render a diff as unified rows: line-number gutter, sign column, highlighted content.
        /// </summary>
        /// <param name="diff">The computed diff.</param>
        /// <param name="filePath">Used only to pick the syntax highlighting language.</param>
        /// <param name="width">Columns available.</param>
        /// <param name="maxRows">Row budget; the middle is elided when the diff is longer.</param>
        /// <param name="theme">Active theme.</param>
        public static IReadOnlyList<StyledLine> RenderDiff(
            FileDiff diff, string? filePath, int width, int maxRows, Themes.Theme? theme = null)
        {
            theme ??= Themes.Theme.Current;
            if (diff is null || diff.Lines.Count == 0) return Array.Empty<StyledLine>();

            var language = LanguageFor(filePath);
            int gutter = GutterWidth(diff);
            var rows = new List<StyledLine>(diff.Lines.Count);

            foreach (var line in diff.Lines)
            {
                rows.Add(RenderDiffLine(line, language, gutter, width, theme));
            }

            return Bound(rows, maxRows, theme);
        }

        /// <summary>
        /// Render file content with a line-number gutter, the way a newly created file should be
        /// shown: a diff of a file that did not exist is all-plus noise, and says nothing a
        /// numbered listing does not say better.
        /// </summary>
        public static IReadOnlyList<StyledLine> RenderContent(
            string? content, string? filePath, int width, int maxRows, Themes.Theme? theme = null)
        {
            theme ??= Themes.Theme.Current;
            if (string.IsNullOrEmpty(content)) return Array.Empty<StyledLine>();

            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            // A trailing newline is punctuation, not a final empty line of content.
            int count = lines.Length;
            while (count > 0 && lines[count - 1].Length == 0) count--;
            if (count == 0) return Array.Empty<StyledLine>();

            var language = LanguageFor(filePath);
            int gutter = Math.Max(MinGutterWidth, count.ToString().Length);
            var rows = new List<StyledLine>(count);

            for (int i = 0; i < count; i++)
            {
                var spans = new List<StyledSpan>
                {
                    new((i + 1).ToString().PadLeft(gutter) + " ", theme.Ghost)
                };
                spans.AddRange(Highlight(lines[i], language, width - gutter - 1, theme, null));
                rows.Add(new StyledLine(spans));
            }

            return Bound(rows, maxRows, theme);
        }

        /// <summary>The "+18 -3" summary line for a change.</summary>
        public static string FormatChangeCounts(int added, int removed)
        {
            if (added > 0 && removed > 0) return $"+{added} -{removed}";
            if (added > 0) return $"+{added}";
            if (removed > 0) return $"-{removed}";
            return "no change";
        }

        private static StyledLine RenderDiffLine(
            DiffLine line, string? language, int gutter, int width, Themes.Theme theme)
        {
            if (line.Kind == DiffLineKind.Gap)
            {
                return StyledLine.Plain(new string(' ', gutter) + " ....", theme.Ghost, DL.CellAttrFlags.None);
            }

            var (sign, signColor, background) = line.Kind switch
            {
                DiffLineKind.Added => ("+", theme.Success, (DL.Rgb24?)theme.DiffAddedBackground),
                DiffLineKind.Removed => ("-", theme.Error, theme.DiffRemovedBackground),
                _ => (" ", theme.Ghost, null)
            };

            // Removed lines are numbered on the old side, everything else on the new side.
            int? number = line.Kind == DiffLineKind.Removed ? line.OldLineNumber : line.NewLineNumber ?? line.OldLineNumber;

            var spans = new List<StyledSpan>
            {
                new(number?.ToString().PadLeft(gutter) ?? new string(' ', gutter), theme.Ghost, DL.CellAttrFlags.None, background),
                new(sign + " ", signColor, DL.CellAttrFlags.Bold, background)
            };

            spans.AddRange(Highlight(line.Text, language, width - gutter - 2, theme, background));
            return new StyledLine(spans);
        }

        // Syntax-highlight one line, padding it out so the row tint covers the full width rather
        // than stopping at the end of the text (a ragged tint is harder to read than none).
        private static IEnumerable<StyledSpan> Highlight(
            string text, string? language, int width, Themes.Theme theme, DL.Rgb24? background)
        {
            text = text.Replace("\t", "    ");
            if (width < 1) width = 1;
            if (text.Length > width) text = text.Substring(0, width);

            var spans = new List<StyledSpan>();
            if (language is null)
            {
                spans.Add(new StyledSpan(text, theme.Code, DL.CellAttrFlags.None, background));
            }
            else
            {
                var palette = SyntaxPalette.FromTheme(theme);
                foreach (var (token, kind) in CodeHighlighter.Tokenize(text, language))
                {
                    spans.Add(new StyledSpan(token, palette.ColorFor(kind), DL.CellAttrFlags.None, background));
                }
                if (spans.Count == 0) spans.Add(new StyledSpan(text, theme.Code, DL.CellAttrFlags.None, background));
            }

            if (background is not null)
            {
                int drawn = spans.Sum(s => s.Text.Length);
                if (drawn < width) spans.Add(new StyledSpan(new string(' ', width - drawn), null, DL.CellAttrFlags.None, background));
            }

            return spans;
        }

        // Keep the head and the tail, like every other bounded body in the feed (#250).
        private static IReadOnlyList<StyledLine> Bound(List<StyledLine> rows, int maxRows, Themes.Theme theme)
        {
            if (maxRows <= 0 || rows.Count <= maxRows) return rows;

            int available = maxRows - 1;
            int tail = Math.Max(1, available / 3);       // the head of a diff is usually the point
            int head = Math.Max(1, available - tail);
            int omitted = rows.Count - head - tail;
            if (omitted <= 0) return rows.Take(maxRows).ToList();

            var kept = new List<StyledLine>(maxRows);
            kept.AddRange(rows.Take(head));
            kept.Add(ToolOutputFormatter.OmissionMarker(omitted, theme));
            kept.AddRange(rows.Skip(rows.Count - tail));
            return kept;
        }

        private static int GutterWidth(FileDiff diff)
        {
            int max = 0;
            foreach (var line in diff.Lines)
            {
                if (line.OldLineNumber is int o && o > max) max = o;
                if (line.NewLineNumber is int n && n > max) max = n;
            }
            return Math.Max(MinGutterWidth, max.ToString().Length);
        }

        /// <summary>
        /// Map a file extension to a highlighting mode. Returns null when the file is not code, so
        /// prose and data files are not tokenized as if they were.
        ///
        /// <see cref="CodeHighlighter"/> has two real modes: Python-style (# comments, single or
        /// double quoted strings) and C-style (// comments, double quoted strings). Languages are
        /// mapped to whichever of the two actually matches their comment and string syntax, rather
        /// than being labelled with a name the tokenizer does not know.
        /// </summary>
        public static string? LanguageFor(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".cs" or ".csx" or ".js" or ".jsx" or ".ts" or ".tsx" or ".java" or ".go" or ".rs"
                    or ".c" or ".h" or ".cpp" or ".hpp" or ".swift" or ".kt" or ".scala" or ".php" => "csharp",
                ".py" or ".pyi" or ".rb" or ".sh" or ".bash" or ".zsh" or ".yml" or ".yaml"
                    or ".toml" => "python",
                _ => null
            };
        }
    }
}
