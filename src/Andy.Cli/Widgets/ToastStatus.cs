using System;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Transient one-line notification rendered over content for a short time.
    /// </summary>
    public sealed class Toast
    {
        private string _text = string.Empty;
        private int _ttlFrames;
        private DL.Rgb24 _fg = new DL.Rgb24(255, 255, 255);
        private DL.Rgb24 _bg = new DL.Rgb24(60, 60, 20);

        /// <summary>Show a toast for the specified number of frames.</summary>
        public void Show(string text, int ttlFrames = 90) { _text = text ?? string.Empty; _ttlFrames = ttlFrames; }
        /// <summary>Advance the internal TTL by one frame.</summary>
        public void Tick() { if (_ttlFrames > 0) _ttlFrames--; }
        /// <summary>True while the toast is still visible.</summary>
        public bool IsVisible => _ttlFrames > 0;
        /// <summary>Render the toast at x,y within the viewport.</summary>
        public void RenderAt(int x, int y, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (!IsVisible) return;
            int w = Math.Max(8, _text.Length + 4);
            b.PushClip(new DL.ClipPush(x, y, w, 1));
            b.DrawRect(new DL.Rect(x, y, w, 1, _bg));
            b.DrawText(new DL.TextRun(x + 2, y, _text, _fg, _bg, DL.CellAttrFlags.Bold));
            b.Pop();
        }
    }

    /// <summary>
    /// Persistent status line at the bottom of the viewport, with optional spinner.
    /// </summary>
    public sealed class StatusLine
    {
        private string _text = string.Empty;
        private bool _spinner;
        private int _tick;
        private readonly char[] _frames = new[] { '|', '/', '-', '\\' };
        private DL.Rgb24 _fg = new DL.Rgb24(180, 180, 180);
        private DL.Rgb24 _bg = new DL.Rgb24(10, 10, 10);

        /// <summary>Set the message and optionally enable a spinner.</summary>
        public void Set(string text, bool spinner = false)
        { _text = text ?? string.Empty; _spinner = spinner; _tick = 0; }

        /// <summary>Advance the spinner one step.</summary>
        public void Tick() { _tick = (_tick + 1) % _frames.Length; }

        /// <summary>Render the status line aligned to the last row of the viewport.</summary>
        public void Render((int Width, int Height) viewport, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int y = Math.Max(0, viewport.Height - 2);
            int x = 0; int w = viewport.Width;
            b.PushClip(new DL.ClipPush(x, y, w, 1));
            b.DrawRect(new DL.Rect(x, y, w, 1, _bg));
            int cx = x + 1;
            if (_spinner)
            {
                b.DrawText(new DL.TextRun(cx, y, _frames[_tick].ToString(), _fg, _bg, DL.CellAttrFlags.Bold));
                cx += 2;
            }
            string msg = _text;
            if (msg.Length > w - 2) msg = msg.Substring(0, w - 2);
            b.DrawText(new DL.TextRun(cx, y, msg, _fg, _bg, DL.CellAttrFlags.None));
            b.Pop();
        }
    }
}
