using System;
using System.Text;
using Andy.Cli.Themes;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Process-wide VIEW state for thinking-block visibility.
    ///
    /// This is a pure presentation toggle: it only changes how already-recorded
    /// <see cref="ThinkingBlockItem"/> items lay out and draw on the next frame.
    /// It does NOT touch the assistant turn, the LLM provider, or any recorded data --
    /// flipping it while a turn is in flight is safe and only reflows the display.
    ///
    /// F4 in Program.cs flips <see cref="Visible"/>. Because feed items read this flag
    /// at measure/render time (not at construction), the change applies retroactively
    /// to every thinking block on screen, both completed and in-flight.
    /// </summary>
    public static class ThinkingView
    {
        // volatile: written on the UI/input thread, read on the render path (same thread
        // today, but marked volatile to make the cross-thread-safe intent explicit and
        // robust if rendering is ever moved off the input thread).
        private static volatile bool _visible = true;

        /// <summary>True when thinking blocks should be rendered; false for zero-height items.</summary>
        public static bool Visible
        {
            get => _visible;
            set => _visible = value;
        }

        /// <summary>Flip visibility and return the new state. Pure view toggle.</summary>
        public static bool Toggle() => _visible = !_visible;
    }

    /// <summary>
    /// A single thinking block rendered as a collapsible section in the feed.
    /// Follows the same streaming pattern as <see cref="StreamingMessageItem"/>:
    /// mutable content via <see cref="AppendContent"/>, finalized by <see cref="Complete"/>.
    ///
    /// When <see cref="ThinkingView.Visible"/> is false, <see cref="MeasureLineCount"/>
    /// returns 0 so the item occupies zero vertical space. Content is always retained in
    /// memory regardless of visibility (F4 toggle must retroactively show/hide).
    /// </summary>
    public sealed class ThinkingBlockItem : IFeedItem
    {
        private readonly StringBuilder _content = new();
        private bool _hidden;

        /// <summary>Append streaming text to the thinking block body.</summary>
        public void AppendContent(string text)
        {
            if (!string.IsNullOrEmpty(text))
                _content.Append(text);
        }

        /// <summary>Mark the thinking block as complete (no more content will arrive).</summary>
        public void Complete()
        {
            // Content is finalized. The field is intentionally not stored since
            // it is not needed for rendering decisions; the block is visible
            // whenever ThinkingView.Visible is true and content is non-empty.
        }

        /// <summary>Temporarily suppress rendering without losing content.</summary>
        public void Hide()
        {
            _hidden = true;
        }

        /// <summary>Get the full accumulated thinking content.</summary>
        public string GetContent() => _content.ToString();

        /// <summary>How many seconds the thinking block took (set on Complete).</summary>
        public TimeSpan? Duration { get; set; }

        /// <summary>
        /// Measure how many lines this item would occupy at a given width.
        /// Returns 0 when <see cref="ThinkingView.Visible"/> is false (zero-cost hidden path).
        /// </summary>
        public int MeasureLineCount(int width)
        {
            if (_hidden || !ThinkingView.Visible)
                return 0;

            // Lines: opening indicator + top border + body + bottom border + closing indicator
            if (width < 4)
                return 0;

            int indent = 2; // leading spaces matching tool execution indentation
            int innerWidth = width - indent - 2; // "  " + "| " ... " |"
            if (innerWidth < 1) innerWidth = 1;

            int bodyLines = 0;
            var text = _content.ToString();
            if (text.Length > 0)
            {
                var lines = text.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Length == 0)
                    {
                        bodyLines++;
                    }
                    else
                    {
                        bodyLines += Math.Max(1, (int)Math.Ceiling((double)line.Length / innerWidth));
                    }
                }
            }
            else
            {
                bodyLines = 1; // at least one empty line while streaming
            }

            return 4 + bodyLines;
        }

        /// <summary>
        /// Render a slice of this thinking block.
        /// </summary>
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines,
            DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (_hidden || !ThinkingView.Visible || width < 4)
                return;

            var theme = Theme.Current;
            int indent = 2;
            int innerWidth = width - indent - 2;
            if (innerWidth < 1) innerWidth = 1;

            // Build the flat list of rendered lines
            var lines = new System.Collections.Generic.List<string>();

            // Line 0: opening indicator
            lines.Add("  [thinking]");

            // Border top
            lines.Add("  +" + new string('-', innerWidth) + "+");

            // Body lines
            var text = _content.ToString();
            if (text.Length > 0)
            {
                var rawLines = text.Split('\n');
                foreach (var rawLine in rawLines)
                {
                    if (rawLine.Length == 0)
                    {
                        lines.Add("  |" + new string(' ', innerWidth) + "|");
                    }
                    else
                    {
                        // Word-wrap the line
                        int pos = 0;
                        while (pos < rawLine.Length)
                        {
                            int chunk = Math.Min(innerWidth, rawLine.Length - pos);
                            var segment = rawLine.Substring(pos, chunk);
                            lines.Add("  |" + PadRight(segment, innerWidth) + "|");
                            pos += chunk;
                        }
                    }
                }
            }
            else
            {
                lines.Add("  |" + new string(' ', innerWidth) + "|");
            }

            // Border bottom
            lines.Add("  +" + new string('-', innerWidth) + "+");

            // Closing indicator
            lines.Add("  [end thinking]");

            // Render the requested slice
            for (int i = 0; i < maxLines && (startLine + i) < lines.Count; i++)
            {
                int lineIdx = startLine + i;
                if (lineIdx < 0 || lineIdx >= lines.Count) continue;

                var line = lines[lineIdx];

                DL.Rgb24 color;
                DL.CellAttrFlags attr;

                if (lineIdx == 0 || lineIdx == lines.Count - 1)
                {
                    // Indicator lines: Ghost color, italic
                    color = theme.Ghost;
                    attr = DL.CellAttrFlags.Italic;
                }
                else if (line.StartsWith("  +") || line.StartsWith("  |"))
                {
                    // Border and body lines
                    color = theme.Border;
                    attr = DL.CellAttrFlags.None;
                }
                else
                {
                    color = theme.TextDim;
                    attr = DL.CellAttrFlags.None;
                }

                var clipped = TruncateToWidth(line, width);
                b.DrawText(new DL.TextRun(x, y + i, clipped, color, null, attr));
            }
        }

        private static string PadRight(string s, int width)
        {
            if (s.Length >= width) return s.Substring(0, width);
            return s + new string(' ', width - s.Length);
        }

        private static string TruncateToWidth(string s, int width)
        {
            if (s.Length <= width) return s;
            return s.Substring(0, width);
        }
    }
}
