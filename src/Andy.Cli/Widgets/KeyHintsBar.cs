using System;
using System.Collections.Generic;
using Andy.Cli.Themes;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Renders a footer of key hints like "[F2] Toggle HUD" with automatic wrapping.
    /// </summary>
    public sealed class KeyHintsBar
    {
        private readonly List<(string key, string action)> _hints = new();

        /// <summary>Sets the ordered list of (key, action) hints.</summary>
        public void SetHints(IEnumerable<(string key, string action)> hints)
        {
            _hints.Clear();
            if (hints == null) return;
            foreach (var h in hints) _hints.Add(h);
        }

        /// <summary>Gets the height needed to render all hints with the given width.</summary>
        public int GetRequiredHeight(int availableWidth)
        {
            if (_hints.Count == 0) return 0;
            if (availableWidth <= 0) return 1; // Minimum height if no space

            // Use the same manual wrapping logic as Render() to ensure consistency
            const int leftMargin = 1;
            int effectiveWidth = availableWidth - leftMargin;
            if (effectiveWidth <= 0) return 1;

            var lines = CalculateWrappedLines(effectiveWidth);
            return Math.Max(1, lines.Count);
        }

        /// <summary>Calculates how hints will wrap into lines given available width.</summary>
        private List<List<(string key, string action, int width)>> CalculateWrappedLines(int availableWidth)
        {
            const int itemGap = 3;
            var lines = new List<List<(string key, string action, int width)>>();
            var currentLine = new List<(string, string, int)>();
            int currentWidth = 0;

            foreach (var (key, action) in _hints)
            {
                int itemWidth = CalculateItemWidth(key, action);
                int gap = currentLine.Count > 0 ? itemGap : 0;
                int projectedWidth = currentWidth + gap + itemWidth;

                // Wrap to next line if this item doesn't fit
                if (currentLine.Count > 0 && projectedWidth > availableWidth)
                {
                    lines.Add(currentLine);
                    currentLine = new List<(string, string, int)>();
                    currentWidth = 0;
                    gap = 0;
                    projectedWidth = itemWidth;
                }

                currentLine.Add((key, action, itemWidth));
                currentWidth = projectedWidth;
            }

            if (currentLine.Count > 0)
            {
                lines.Add(currentLine);
            }

            return lines;
        }

        /// <summary>Renders into the bottom rows of the viewport with automatic wrapping.</summary>
        public void Render((int Width, int Height) viewport, DL.DisplayList baseDl, DL.DisplayListBuilder b, int reservedRightWidth = 0)
        {
            if (_hints.Count == 0) return;
            var theme = Theme.Current;

            // Calculate available width for hints (excluding reserved area and margins)
            const int leftMargin = 1;
            const int itemGap = 3;
            int availableWidth = viewport.Width - reservedRightWidth - leftMargin;
            if (availableWidth <= 0) return; // No space to render

            // Use shared wrapping logic to ensure consistency with GetRequiredHeight()
            var lines = CalculateWrappedLines(availableWidth);

            // Calculate height (1 line per row)
            int requiredHeight = Math.Max(1, lines.Count);
            int startY = Math.Max(0, viewport.Height - requiredHeight);

            // Draw background for all rows
            b.PushClip(new DL.ClipPush(0, startY, viewport.Width - reservedRightWidth, requiredHeight));
            b.DrawRect(new DL.Rect(0, startY, viewport.Width - reservedRightWidth, requiredHeight, theme.KeyHintsBackground));

            // Render each line
            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                int x = leftMargin;
                int y = startY + lineIndex;

                for (int itemIndex = 0; itemIndex < line.Count; itemIndex++)
                {
                    var (key, action, _) = line[itemIndex];

                    // Add gap before items (except first)
                    if (itemIndex > 0)
                    {
                        x += itemGap;
                    }

                    RenderHintItem(b, x, y, key, action, theme);
                    x += CalculateItemWidth(key, action);
                }
            }

            b.Pop();
        }

        private void RenderHintItem(DL.DisplayListBuilder b, int x, int y, string key, string action, Theme theme)
        {
            string ks = key ?? string.Empty;
            string txt = action ?? string.Empty;
            int cx = x;

            // If key is empty, this is a plain text item (like a URL)
            if (string.IsNullOrEmpty(ks))
            {
                // Check if the text contains a URL - render with special color
                if (txt.Contains("http://") || txt.Contains("https://"))
                {
                    // Use cyan color and underline for URLs
                    b.DrawText(new DL.TextRun(cx, y, txt, new DL.Rgb24(100, 200, 255), theme.KeyHintsBackground, DL.CellAttrFlags.Underline));
                }
                else
                {
                    b.DrawText(new DL.TextRun(cx, y, txt, theme.TextDim, theme.KeyHintsBackground, DL.CellAttrFlags.None));
                }
            }
            else
            {
                // Render like: [F1] Help
                string bracket = "[" + ks + "] ";
                b.DrawText(new DL.TextRun(cx, y, bracket, theme.KeyHighlight, theme.KeyHintsBackground, DL.CellAttrFlags.Bold));
                cx += bracket.Length;

                b.DrawText(new DL.TextRun(cx, y, txt, theme.TextDim, theme.KeyHintsBackground, DL.CellAttrFlags.None));
            }
        }

        private int CalculateItemWidth(string key, string action)
        {
            string ks = key ?? string.Empty;
            string txt = action ?? string.Empty;

            if (string.IsNullOrEmpty(ks))
            {
                // Plain text item (e.g., URL)
                return txt.Length;
            }
            else
            {
                // Key hint item: "[KEY] Action"
                return ks.Length + 3 + txt.Length; // [KEY] = key.Length + 3 characters
            }
        }
    }
}
