using System;
using System.Collections.Generic;
using Andy.Cli.Services;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>Whether a rendered file change created a new file or updated an existing one.</summary>
    public enum FileChangeKind
    {
        /// <summary>A new file was created.</summary>
        Create,
        /// <summary>An existing file was modified.</summary>
        Update
    }

    /// <summary>
    /// Renders a file write/update as a git-style unified diff: a "Create(path)"/"Update(path)"
    /// header, an "+added -removed" summary, and the colored +/- hunks with a line-number gutter.
    /// Rows are precomputed so <see cref="MeasureLineCount"/> exactly equals the rows drawn.
    /// </summary>
    public sealed class FileDiffItem : IFeedItem
    {
        private enum RowKind { Header, Summary, Context, Added, Removed, Gap, More }

        private readonly struct Row
        {
            public readonly RowKind Kind;
            public readonly string Gutter; // right-aligned line-number column (already padded), or spaces
            public readonly string Body;   // sign + content (e.g. "+ foo"), or full text for header/summary
            public Row(RowKind kind, string gutter, string body) { Kind = kind; Gutter = gutter; Body = body; }
        }

        // Keep the feed from being flooded by a huge diff; hunk-gap collapsing already trims
        // unchanged regions, so this only bites on genuinely enormous changes.
        private const int MaxBodyRows = 400;

        private readonly List<Row> _rows = new();
        private readonly int _added;
        private readonly int _removed;
        private readonly int _gutterWidth;

        public FileDiffItem(string displayPath, FileChangeKind kind, FileDiff diff)
        {
            _added = diff.AddedCount;
            _removed = diff.RemovedCount;
            _gutterWidth = GutterWidth(diff);

            var verb = kind == FileChangeKind.Create ? "Create" : "Update";
            _rows.Add(new Row(RowKind.Header, "", $"{verb}({displayPath})"));
            _rows.Add(new Row(RowKind.Summary, "", "")); // rendered specially from _added/_removed

            var pad = new string(' ', _gutterWidth);
            int body = 0;
            for (int i = 0; i < diff.Lines.Count; i++)
            {
                if (body >= MaxBodyRows)
                {
                    int remaining = diff.Lines.Count - i;
                    _rows.Add(new Row(RowKind.More, pad, $"  ... {remaining} more diff line(s)"));
                    break;
                }

                var dl = diff.Lines[i];
                switch (dl.Kind)
                {
                    case DiffLineKind.Gap:
                        _rows.Add(new Row(RowKind.Gap, pad, "  ...."));
                        break;
                    case DiffLineKind.Added:
                        _rows.Add(new Row(RowKind.Added, FormatGutter(dl.NewLineNumber), "+ " + dl.Text));
                        break;
                    case DiffLineKind.Removed:
                        _rows.Add(new Row(RowKind.Removed, FormatGutter(dl.OldLineNumber), "- " + dl.Text));
                        break;
                    default:
                        _rows.Add(new Row(RowKind.Context, FormatGutter(dl.NewLineNumber ?? dl.OldLineNumber), "  " + dl.Text));
                        break;
                }
                body++;
            }
        }

        private static int GutterWidth(FileDiff diff)
        {
            int max = 0;
            foreach (var l in diff.Lines)
            {
                if (l.OldLineNumber is int o && o > max) max = o;
                if (l.NewLineNumber is int n && n > max) max = n;
            }
            return Math.Max(3, max.ToString().Length);
        }

        private string FormatGutter(int? n)
            => n.HasValue ? n.Value.ToString().PadLeft(_gutterWidth) : new string(' ', _gutterWidth);

        /// <inheritdoc />
        public int MeasureLineCount(int width) => _rows.Count;

        /// <inheritdoc />
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            var theme = Themes.Theme.Current;
            int printed = 0;
            for (int i = startLine; i < _rows.Count && printed < maxLines; i++)
            {
                var row = _rows[i];
                int ry = y + printed;
                // Clear the row so scrolling doesn't leave residue.
                b.DrawRect(new DL.Rect(x, ry, width, 1, theme.Background));

                if (row.Kind == RowKind.Header)
                {
                    DrawClipped(b, x, ry, width, row.Body, theme.Accent, theme.Background, DL.CellAttrFlags.Bold);
                }
                else if (row.Kind == RowKind.Summary)
                {
                    var addStr = $"+{_added}";
                    DrawClipped(b, x, ry, width, addStr, theme.Success, theme.Background, DL.CellAttrFlags.None);
                    var remStr = $" -{_removed}";
                    if (addStr.Length < width)
                        DrawClipped(b, x + addStr.Length, ry, width - addStr.Length, remStr, theme.Error, theme.Background, DL.CellAttrFlags.None);
                }
                else
                {
                    // Gutter (dim), then the sign+content in the row's color.
                    DrawClipped(b, x, ry, width, row.Gutter, theme.Ghost, theme.Background, DL.CellAttrFlags.None);
                    int bodyX = x + row.Gutter.Length + 1;
                    int bodyWidth = width - row.Gutter.Length - 1;
                    if (bodyWidth > 0)
                    {
                        var color = row.Kind switch
                        {
                            RowKind.Added => theme.Success,
                            RowKind.Removed => theme.Error,
                            _ => theme.TextDim
                        };
                        DrawClipped(b, bodyX, ry, bodyWidth, row.Body, color, theme.Background, DL.CellAttrFlags.None);
                    }
                }
                printed++;
            }
        }

        private static void DrawClipped(DL.DisplayListBuilder b, int x, int y, int maxWidth, string text, DL.Rgb24 fg, DL.Rgb24? bg, DL.CellAttrFlags attr)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return;
            // Tabs would desync the cursor advance from the glyph count; expand to spaces.
            text = text.Replace("\t", "    ");
            if (text.Length > maxWidth) text = text.Substring(0, maxWidth);
            b.DrawText(new DL.TextRun(x, y, text, fg, bg, attr));
        }
    }
}
