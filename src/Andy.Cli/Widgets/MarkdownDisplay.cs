using System;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Minimal Markdown display: supports headings (#, ##), fenced code blocks (```), and paragraphs.
    /// Includes follow-tail with bottom alignment and optional scroll animation.
    /// </summary>
    public sealed class MarkdownDisplay
    {
        private string _markdown = string.Empty;
        private string[] _linesCache = Array.Empty<string>();
        private int _prevLineCount = 0;
        private int _scrollOffset = 0; // 0 = bottom; >0 = lines above bottom
        private int _animRemaining = 0; // lines left to animate in
        private int _animSpeed = 2;     // lines per frame

        private DL.Rgb24 _fg = new DL.Rgb24(220, 220, 220);
        private DL.Rgb24? _bg = null; // Transparent background
        private DL.Rgb24 _h = new DL.Rgb24(200, 200, 80);

        /// <summary>When true and not scrolled up, keep view pinned to bottom.</summary>
        public bool FollowTail { get; set; } = true;
        /// <summary>Enable simple scroll animation when new lines arrive.</summary>
        public bool EnableScrollAnimation { get; set; } = true;

        /// <summary>Replace markdown content. Appending will animate if enabled and following tail.</summary>
        public void SetText(string md)
        {
            _markdown = md ?? string.Empty;
            _linesCache = _markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int len = _linesCache.Length;
            if (FollowTail && _scrollOffset == 0 && EnableScrollAnimation)
            {
                int added = Math.Max(0, len - _prevLineCount);
                _animRemaining = Math.Min(_animRemaining + added, Math.Max(0, len));
            }
            _prevLineCount = len;
        }

        /// <summary>Scroll viewport by delta lines; pageSize used for PgUp/PgDn. Returns current offset.</summary>
        public int ScrollLines(int delta, int pageSize)
        {
            if (_linesCache.Length == 0) return _scrollOffset;
            int maxOffset = Math.Max(0, _linesCache.Length - 1);
            if (delta == int.MaxValue) delta = pageSize; // convenience
            if (delta == int.MinValue) delta = -pageSize;
            _scrollOffset = Math.Max(0, Math.Min(maxOffset, _scrollOffset + delta));
            FollowTail = _scrollOffset == 0;
            return _scrollOffset;
        }

        /// <summary>Advance animation state one frame.</summary>
        public void Tick()
        {
            if (_animRemaining > 0)
            {
                _animRemaining = Math.Max(0, _animRemaining - _animSpeed);
            }
        }

        /// <summary>Render markdown within rect. Bottom-aligns when following tail.</summary>
        public void Render(in L.Rect rect, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;
            b.PushClip(new DL.ClipPush(x, y, w, h));
            // No background rectangle - use transparent terminal background
            var lines = _linesCache;
            int total = lines.Length;
            int visible = Math.Min(h, total);
            int startIdx;
            if (FollowTail && _scrollOffset == 0)
            {
                // Show the last 'visible' lines, animated from above
                int baseStart = Math.Max(0, total - visible);
                startIdx = Math.Max(0, baseStart - _animRemaining);
            }
            else
            {
                startIdx = Math.Max(0, total - visible - _scrollOffset);
            }
            int cy = y + Math.Max(0, h - Math.Min(visible, total - startIdx)); // bottom-align
            bool inCode = false;
            for (int idx = startIdx; idx < total && cy < y + h; idx++)
            {
                string line = lines[idx];
                if (line.StartsWith("```")) { inCode = !inCode; continue; }
                if (inCode)
                {
                    DrawLine(line, new DL.Rgb24(180, 180, 180), null, x + 1, cy++, w - 2, b);
                    continue;
                }
                if (line.StartsWith("# "))
                { DrawLine(line.Substring(2), _h, null, x + 1, cy++, w - 2, b, DL.CellAttrFlags.Bold); continue; }
                if (line.StartsWith("## "))
                { DrawLine(line.Substring(3), _h, null, x + 1, cy++, w - 2, b, DL.CellAttrFlags.Bold); continue; }
                DrawLine(line, _fg, null, x + 1, cy++, w - 2, b);
            }
            b.Pop();
        }

        private static void DrawLine(string text, DL.Rgb24 fg, DL.Rgb24? bg, int x, int y, int w, DL.DisplayListBuilder b, DL.CellAttrFlags attr = DL.CellAttrFlags.None)
        {
            if (w <= 0) return;
            string t = text.Length > w ? text.Substring(0, w) : text;
            b.DrawText(new DL.TextRun(x, y, t, fg, bg, attr));
        }
    }
}
