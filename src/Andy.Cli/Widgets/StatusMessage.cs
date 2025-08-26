using System;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>Dynamic status message widget for showing current AI interaction state.</summary>
    public sealed class StatusMessage
    {
        private string _message = "Ready to assist";
        private DateTime _lastUpdate = DateTime.Now;
        private DL.Rgb24 _fg = new DL.Rgb24(150, 200, 255);
        private DL.Rgb24 _bg = new DL.Rgb24(0, 0, 0);
        private bool _isAnimated = false;
        
        /// <summary>Set the current status message.</summary>
        public void SetMessage(string message, bool animated = false)
        {
            _message = message;
            _isAnimated = animated;
            _lastUpdate = DateTime.Now;
        }
        
        /// <summary>Set the colors for the status message.</summary>
        public void SetColors(DL.Rgb24 fg, DL.Rgb24 bg)
        {
            _fg = fg;
            _bg = bg;
        }
        
        /// <summary>Render the status message at the specified position.</summary>
        public void RenderAt(int x, int y, int maxWidth, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            string displayMessage = _message;
            
            // Add animated dots for processing states
            if (_isAnimated)
            {
                double elapsed = (DateTime.Now - _lastUpdate).TotalSeconds;
                int dotCount = ((int)(elapsed * 2) % 4); // Cycle through 0-3 dots every 2 seconds
                displayMessage += new string('.', dotCount);
            }
            
            // Truncate if too long
            if (displayMessage.Length > maxWidth)
            {
                displayMessage = displayMessage.Substring(0, maxWidth - 3) + "...";
            }
            
            // Render the message
            for (int i = 0; i < displayMessage.Length && i < maxWidth; i++)
            {
                char ch = displayMessage[i];
                var attrs = (ch == '.' && _isAnimated) ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None;
                
                b.DrawText(new DL.TextRun(x + i, y, ch.ToString(), _fg, _bg, attrs));
            }
        }
        
        /// <summary>Get the current message length (without animated dots).</summary>
        public int GetBaseLength() => _message.Length;
    }
}