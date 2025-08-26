using System;
using System.Collections.Generic;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Legacy CommandOutputView providing a similar API as CommandOutput.
    /// </summary>
    public sealed class CommandOutputView
    {
        private readonly List<string> _lines = new();
        private int _scroll; // number of lines from bottom (0 = follow tail)
        private DL.Rgb24 _bg = new DL.Rgb24(0,0,0);
        private DL.Rgb24 _fg = new DL.Rgb24(200,200,200);

        /// <summary>Adds a line to the view.</summary>
        public void Append(string line)
        {
            _lines.Add(line ?? string.Empty);
            if (_scroll == 0) { /* follow tail */ }
        }
        /// <summary>Adds multiple lines.</summary>
        public void AppendMany(IEnumerable<string> lines)
        { foreach (var l in lines) Append(l); }

        /// <summary>Scroll by delta within the bounds.</summary>
        public void ScrollLines(int delta, int viewportH)
        {
            int maxScroll = Math.Max(0, _lines.Count - viewportH);
            _scroll = Math.Max(0, Math.Min(maxScroll, _scroll + delta));
        }
        /// <summary>Follow the tail (latest lines).</summary>
        public void FollowTail() { _scroll = 0; }

        /// <summary>Render within the given rectangle.</summary>
        public void Render(in L.Rect rect, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int x=(int)rect.X, y=(int)rect.Y, w=(int)rect.Width, h=(int)rect.Height;
            b.PushClip(new DL.ClipPush(x,y,w,h));
            b.DrawRect(new DL.Rect(x,y,w,h,_bg));
            int start = Math.Max(0, _lines.Count - h - _scroll);
            for (int i=0;i<h;i++)
            {
                int idx = start + i; if (idx >= _lines.Count) break;
                string line = _lines[idx];
                if (line.Length > w) line = line.Substring(0, w);
                b.DrawText(new DL.TextRun(x, y + i, line, _fg, _bg, DL.CellAttrFlags.None));
            }
            b.Pop();
        }
    }
}
