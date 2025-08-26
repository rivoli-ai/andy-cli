using System;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>Static separator for end-of-response marking with token information.</summary>
    public sealed class ResponseSeparatorItem : IFeedItem
    {
        private readonly int _inputTokens;
        private readonly int _outputTokens;
        private readonly string _pattern;
        
        public ResponseSeparatorItem(int inputTokens = 0, int outputTokens = 0, string pattern = "━━ ◆ ━━")
        {
            _inputTokens = inputTokens;
            _outputTokens = outputTokens;
            _pattern = pattern;
        }
        
        public int MeasureLineCount(int width) => 1;
        
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (startLine > 0 || maxLines < 1) return;
            
            // Static colors
            var baseColor = new DL.Rgb24(120, 140, 160);
            var accentColor = new DL.Rgb24(180, 200, 255);
            var tokenColor = new DL.Rgb24(150, 170, 140);
            
            // Build the display string
            string tokenInfo = "";
            if (_inputTokens > 0 || _outputTokens > 0)
            {
                tokenInfo = $" ({_inputTokens}→{_outputTokens} tokens)";
            }
            
            string fullPattern = _pattern + tokenInfo;
            
            // Center the pattern
            int patternLength = fullPattern.Length;
            int startX = x + Math.Max(0, (width - patternLength) / 2);
            
            // Render the main pattern
            for (int i = 0; i < _pattern.Length && startX + i < x + width; i++)
            {
                char ch = _pattern[i];
                var color = (ch == '◆') ? accentColor : baseColor;
                var attrs = (ch == '◆') ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None;
                
                b.DrawText(new DL.TextRun(startX + i, y, ch.ToString(), color, new DL.Rgb24(0,0,0), attrs));
            }
            
            // Render token information
            if (!string.IsNullOrEmpty(tokenInfo))
            {
                int tokenStartX = startX + _pattern.Length;
                for (int i = 0; i < tokenInfo.Length && tokenStartX + i < x + width; i++)
                {
                    char ch = tokenInfo[i];
                    b.DrawText(new DL.TextRun(tokenStartX + i, y, ch.ToString(), tokenColor, new DL.Rgb24(0,0,0), DL.CellAttrFlags.None));
                }
            }
        }
    }
}