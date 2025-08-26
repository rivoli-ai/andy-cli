using System;
using System.Collections.Generic;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Renders a single-line footer of key hints like "[F2] Toggle HUD".
    /// </summary>
    public sealed class KeyHintsBar
    {
        private readonly List<(string key, string action)> _hints = new();
        private DL.Rgb24 _bg = new DL.Rgb24(15, 15, 15);
        private DL.Rgb24 _fg = new DL.Rgb24(180, 180, 180);
        private DL.Rgb24 _key = new DL.Rgb24(200, 200, 80);

        /// <summary>Sets the ordered list of (key, action) hints.</summary>
        public void SetHints(IEnumerable<(string key, string action)> hints)
        {
            _hints.Clear();
            if (hints == null) return;
            foreach (var h in hints) _hints.Add(h);
        }

        /// <summary>Sets colors: text foreground, background, and key highlight color.</summary>
        public void SetColors(DL.Rgb24 fg, DL.Rgb24 bg, DL.Rgb24 keyColor)
        { _fg = fg; _bg = bg; _key = keyColor; }

        /// <summary>Renders into the last row of the viewport.</summary>
        public void Render((int Width, int Height) viewport, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (_hints.Count == 0) return;
            int y = Math.Max(0, viewport.Height - 1);
            int x = 0; int w = viewport.Width;
            b.PushClip(new DL.ClipPush(x, y, w, 1));
            b.DrawRect(new DL.Rect(x, y, w, 1, _bg));
            int cx = x + 1;
            for (int i = 0; i < _hints.Count && cx < x + w - 1; i++)
            {
                var (k, a) = _hints[i];
                string ks = k ?? string.Empty;
                string txt = a ?? string.Empty;
                // Render like: [F1] Help   [Q] Quit
                string bracket = "[" + ks + "] ";
                b.DrawText(new DL.TextRun(cx, y, bracket, _key, _bg, DL.CellAttrFlags.Bold));
                cx += bracket.Length;
                if (cx >= x + w - 1) break;
                int room = x + w - 1 - cx;
                string clipped = txt.Length > room ? txt.Substring(0, room) : txt;
                b.DrawText(new DL.TextRun(cx, y, clipped, _fg, _bg, DL.CellAttrFlags.None));
                cx += clipped.Length + 3; // spacing
            }
            b.Pop();
        }
    }
}
