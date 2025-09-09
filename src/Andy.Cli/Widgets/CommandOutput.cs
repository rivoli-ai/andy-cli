using System;
using System.Collections.Generic;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Scrollable textual command output. Append lines and render a viewport with vertical scrolling.
    /// </summary>
    public sealed class CommandOutput
    {
        private readonly List<string> _lines = new();
        private int _scroll;
        private DL.Rgb24 _fg = new DL.Rgb24(220, 220, 220);
        private DL.Rgb24 _bg = new DL.Rgb24(0, 0, 0);

        /// <summary>Adds a new line to the output buffer (kept up to ~5000 lines).</summary>
        public void Append(string line)
        { if (line == null) return; _lines.Add(line); if (_lines.Count > 5000) _lines.RemoveRange(0, _lines.Count - 5000); }
        /// <summary>Clears the buffer and resets scroll.</summary>
        public void Clear() { _lines.Clear(); _scroll = 0; }
        /// <summary>Adjusts scroll by delta within bounds based on viewport height.</summary>
        public void Scroll(int delta, int viewportH)
        {
            int maxScroll = Math.Max(0, _lines.Count - viewportH);
            _scroll = Math.Max(0, Math.Min(maxScroll, _scroll + delta));
        }

        /// <summary>Renders within the given rectangle using the provided DisplayList builder.</summary>
        public void Render(in L.Rect rect, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;
            if (w <= 0 || h <= 0) return;
            b.PushClip(new DL.ClipPush(x, y, w, h));
            b.DrawRect(new DL.Rect(x, y, w, h, _bg));
            int start = Math.Max(0, Math.Min(_scroll, Math.Max(0, _lines.Count - h)));
            for (int i = 0; i < h; i++)
            {
                int idx = start + i;
                if (idx >= _lines.Count) break;
                string line = _lines[idx];
                int room = Math.Max(0, w - 1);
                if (line.Length > room) line = line.Substring(0, room);
                b.DrawText(new DL.TextRun(x, y + i, line, _fg, _bg, DL.CellAttrFlags.None));
            }
            b.Pop();
        }
    }
}
