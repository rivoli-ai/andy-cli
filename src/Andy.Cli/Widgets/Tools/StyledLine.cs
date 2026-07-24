using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>A run of characters sharing one foreground color and attribute set.</summary>
    /// <param name="Text">The characters.</param>
    /// <param name="Foreground">Foreground color, or null to inherit the caller's default.</param>
    /// <param name="Attributes">Bold/italic/underline flags.</param>
    public readonly record struct StyledSpan(string Text, DL.Rgb24? Foreground, DL.CellAttrFlags Attributes)
    {
        /// <summary>An unstyled run that inherits the caller's color.</summary>
        public static StyledSpan Plain(string text) => new(text, null, DL.CellAttrFlags.None);
    }

    /// <summary>
    /// One display row made of styled spans. Tool output carries per-character color once ANSI
    /// has been decoded (issue #250), so it cannot be represented as a plain string all the way
    /// to the renderer without throwing the colors away.
    /// </summary>
    public sealed class StyledLine
    {
        private readonly List<StyledSpan> _spans;

        /// <summary>Create a line from spans, dropping empty ones.</summary>
        public StyledLine(IEnumerable<StyledSpan> spans)
        {
            _spans = spans.Where(s => !string.IsNullOrEmpty(s.Text)).ToList();
            Width = _spans.Sum(s => s.Text.Length);
        }

        /// <summary>The spans, in display order.</summary>
        public IReadOnlyList<StyledSpan> Spans => _spans;

        /// <summary>Total character count across all spans.</summary>
        public int Width { get; }

        /// <summary>True when the line has no characters.</summary>
        public bool IsEmpty => Width == 0;

        /// <summary>An empty line (renders as a blank row).</summary>
        public static StyledLine Empty { get; } = new(Array.Empty<StyledSpan>());

        /// <summary>A single-style line.</summary>
        public static StyledLine Plain(string text, DL.Rgb24? foreground = null,
            DL.CellAttrFlags attributes = DL.CellAttrFlags.None)
            => new(new[] { new StyledSpan(text ?? string.Empty, foreground, attributes) });

        /// <summary>The characters with styling discarded, for measuring and for tests.</summary>
        public string Text
        {
            get
            {
                var sb = new StringBuilder(Width);
                foreach (var span in _spans) sb.Append(span.Text);
                return sb.ToString();
            }
        }

        /// <summary>A new line holding <paramref name="length"/> characters from <paramref name="start"/>.</summary>
        public StyledLine Slice(int start, int length)
        {
            if (length <= 0 || start >= Width) return Empty;
            start = Math.Max(0, start);
            length = Math.Min(length, Width - start);

            var result = new List<StyledSpan>();
            int cursor = 0;
            foreach (var span in _spans)
            {
                int spanStart = cursor;
                int spanEnd = cursor + span.Text.Length;
                cursor = spanEnd;

                if (spanEnd <= start) continue;
                if (spanStart >= start + length) break;

                int from = Math.Max(0, start - spanStart);
                int to = Math.Min(span.Text.Length, start + length - spanStart);
                if (to > from)
                    result.Add(span with { Text = span.Text.Substring(from, to - from) });
            }
            return new StyledLine(result);
        }

        /// <summary>Prepend a span (used for gutters and continuation indents).</summary>
        public StyledLine WithPrefix(StyledSpan prefix)
            => new(new[] { prefix }.Concat(_spans));

        /// <summary>Append a span (used for trailing metrics such as a duration).</summary>
        public StyledLine WithSuffix(StyledSpan suffix)
            => new(_spans.Concat(new[] { suffix }));

        /// <summary>
        /// Word-wrap into rows of at most <paramref name="width"/> characters, preserving span
        /// styling across the break. Over-long tokens (paths, URLs, base64 blobs) are hard-broken
        /// rather than truncated, so nothing is silently lost.
        /// </summary>
        public IReadOnlyList<StyledLine> Wrap(int width)
        {
            if (width < 1) width = 1;
            if (Width <= width) return new[] { this };
            return WrapRanges(Text, width).Select(r => Slice(r.Start, r.Length)).ToList();
        }

        /// <summary>
        /// Compute the character ranges a word-wrap of <paramref name="text"/> produces. The space
        /// at a break point is consumed by the break and belongs to no row, which is why this
        /// returns ranges instead of substrings - the caller maps them back onto styled spans.
        /// </summary>
        public static IReadOnlyList<(int Start, int Length)> WrapRanges(string text, int width)
        {
            if (width < 1) width = 1;
            var ranges = new List<(int, int)>();
            if (string.IsNullOrEmpty(text)) return ranges;

            int lineStart = 0;      // first character of the row being built
            int lastBreak = -1;     // index of the most recent space that could end this row

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == ' ') lastBreak = i;

                if (i - lineStart + 1 <= width) continue;

                // The row is now one character too long: break at the last space if there is one
                // inside this row, otherwise hard-break mid-token.
                if (lastBreak > lineStart)
                {
                    ranges.Add((lineStart, lastBreak - lineStart));
                    lineStart = lastBreak + 1;
                }
                else
                {
                    ranges.Add((lineStart, width));
                    lineStart += width;
                }
                lastBreak = -1;
                // Re-examine the current character against the new row.
                i = lineStart - 1;
            }

            if (lineStart < text.Length) ranges.Add((lineStart, text.Length - lineStart));
            if (ranges.Count == 0) ranges.Add((0, text.Length));
            return ranges;
        }

        /// <summary>
        /// Draw this line at (<paramref name="x"/>, <paramref name="y"/>), clipped to
        /// <paramref name="maxWidth"/> columns. Spans without a color of their own use
        /// <paramref name="defaultForeground"/>.
        /// </summary>
        public void Render(DL.DisplayListBuilder b, int x, int y, int maxWidth,
            DL.Rgb24 defaultForeground, DL.Rgb24? background = null)
        {
            if (maxWidth <= 0) return;
            int drawn = 0;
            foreach (var span in _spans)
            {
                if (drawn >= maxWidth) break;
                var text = span.Text;
                if (drawn + text.Length > maxWidth) text = text.Substring(0, maxWidth - drawn);
                b.DrawText(new DL.TextRun(x + drawn, y, text,
                    span.Foreground ?? defaultForeground, background, span.Attributes));
                drawn += text.Length;
            }
        }
    }
}
