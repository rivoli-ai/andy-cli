using System;
using System.Collections.Generic;
using Andy.Cli.Themes;
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

        /// <summary>Sets the ordered list of (key, action) hints.</summary>
        public void SetHints(IEnumerable<(string key, string action)> hints)
        {
            _hints.Clear();
            if (hints == null) return;
            foreach (var h in hints) _hints.Add(h);
        }

        /// <summary>Renders into the last row of the viewport.</summary>
        public void Render((int Width, int Height) viewport, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (_hints.Count == 0) return;
            var theme = Theme.Current;
            int y = Math.Max(0, viewport.Height - 1);
            int x = 0; int w = viewport.Width;
            b.PushClip(new DL.ClipPush(x, y, w, 1));
            b.DrawRect(new DL.Rect(x, y, w, 1, theme.KeyHintsBackground));
            int cx = x + 1;
            for (int i = 0; i < _hints.Count && cx < x + w - 1; i++)
            {
                var (k, a) = _hints[i];
                string ks = k ?? string.Empty;
                string txt = a ?? string.Empty;
                // Render like: [F1] Help   [Q] Quit
                string bracket = "[" + ks + "] ";
                b.DrawText(new DL.TextRun(cx, y, bracket, theme.KeyHighlight, theme.KeyHintsBackground, DL.CellAttrFlags.Bold));
                cx += bracket.Length;
                if (cx >= x + w - 1) break;
                int room = x + w - 1 - cx;
                string clipped = txt.Length > room ? txt.Substring(0, room) : txt;
                b.DrawText(new DL.TextRun(cx, y, clipped, theme.TextDim, theme.KeyHintsBackground, DL.CellAttrFlags.None));
                cx += clipped.Length + 3; // spacing
            }
            b.Pop();
        }
    }
}
