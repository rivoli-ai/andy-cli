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
        public void Render((int Width, int Height) viewport, DL.DisplayList baseDl, DL.DisplayListBuilder b, int reservedRightWidth = 0)
        {
            if (_hints.Count == 0) return;
            var theme = Theme.Current;
            int y = Math.Max(0, viewport.Height - 1);
            int x = 0; int w = viewport.Width;

            // Calculate available width for hints (excluding reserved area)
            int availableWidth = w - reservedRightWidth;
            int maxX = x + availableWidth - 1; // Reserve space on the right

            // Only clip and draw background for the hints area (not the reserved area)
            b.PushClip(new DL.ClipPush(x, y, availableWidth, 1));
            b.DrawRect(new DL.Rect(x, y, availableWidth, 1, theme.KeyHintsBackground));
            int cx = x + 1;
            for (int i = 0; i < _hints.Count && cx < maxX; i++)
            {
                var (k, a) = _hints[i];
                string ks = k ?? string.Empty;
                string txt = a ?? string.Empty;

                // If key is empty, this is a plain text item (like a URL)
                if (string.IsNullOrEmpty(ks))
                {
                    int txtRoom = maxX - cx;
                    string txtClipped = txt.Length > txtRoom ? txt.Substring(0, txtRoom) : txt;

                    // Check if the text contains a URL
                    if (txt.Contains("http://") || txt.Contains("https://"))
                    {
                        // Extract URL from text like "Instrumentation: http://localhost:5555"
                        var urlStart = txt.IndexOf("http");
                        if (urlStart >= 0)
                        {
                            var prefix = txt.Substring(0, urlStart);
                            var url = txt.Substring(urlStart);

                            // Render prefix normally
                            if (prefix.Length > 0 && cx < maxX)
                            {
                                int prefixRoom = maxX - cx;
                                var clippedPrefix = prefix.Length > prefixRoom ? prefix.Substring(0, prefixRoom) : prefix;
                                b.DrawText(new DL.TextRun(cx, y, clippedPrefix, theme.TextDim, theme.KeyHintsBackground, DL.CellAttrFlags.None));
                                cx += clippedPrefix.Length;
                            }

                            // Render URL with hyperlink (OSC 8) and underline
                            if (cx < maxX)
                            {
                                int urlRoom = maxX - cx;
                                var clippedUrl = url.Length > urlRoom ? url.Substring(0, urlRoom) : url;

                                // OSC 8 hyperlink: \e]8;;URL\e\\TEXT\e]8;;\e\\
                                var hyperlinkStart = $"\u001b]8;;{url}\u001b\\";
                                var hyperlinkEnd = "\u001b]8;;\u001b\\";
                                var hyperlinkText = hyperlinkStart + clippedUrl + hyperlinkEnd;

                                // Use cyan color and underline for the link
                                b.DrawText(new DL.TextRun(cx, y, hyperlinkText, new DL.Rgb24(100, 200, 255), theme.KeyHintsBackground, DL.CellAttrFlags.Underline));
                                cx += clippedUrl.Length;
                            }
                        }
                        else
                        {
                            b.DrawText(new DL.TextRun(cx, y, txtClipped, theme.TextDim, theme.KeyHintsBackground, DL.CellAttrFlags.None));
                            cx += txtClipped.Length;
                        }
                    }
                    else
                    {
                        b.DrawText(new DL.TextRun(cx, y, txtClipped, theme.TextDim, theme.KeyHintsBackground, DL.CellAttrFlags.None));
                        cx += txtClipped.Length;
                    }
                    cx += 3; // spacing
                    continue;
                }

                // Render like: [F1] Help   [Q] Quit
                string bracket = "[" + ks + "] ";
                b.DrawText(new DL.TextRun(cx, y, bracket, theme.KeyHighlight, theme.KeyHintsBackground, DL.CellAttrFlags.Bold));
                cx += bracket.Length;
                if (cx >= maxX) break;
                int room = maxX - cx;
                string clipped = txt.Length > room ? txt.Substring(0, room) : txt;
                b.DrawText(new DL.TextRun(cx, y, clipped, theme.TextDim, theme.KeyHintsBackground, DL.CellAttrFlags.None));
                cx += clipped.Length + 3; // spacing
            }
            b.Pop();
        }
    }
}
