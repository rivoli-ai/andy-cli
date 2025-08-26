using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>Token counter widget to display total tokens used in the session.</summary>
    public sealed class TokenCounter
    {
        private int _totalInputTokens;
        private int _totalOutputTokens;
        private DL.Rgb24 _fg = new DL.Rgb24(180, 180, 180);
        private DL.Rgb24 _bg = new DL.Rgb24(0, 0, 0);
        private DL.Rgb24 _accent = new DL.Rgb24(120, 200, 120);
        
        /// <summary>Add tokens to the running total.</summary>
        public void AddTokens(int inputTokens, int outputTokens)
        {
            _totalInputTokens += inputTokens;
            _totalOutputTokens += outputTokens;
        }
        
        /// <summary>Reset the token counters.</summary>
        public void Reset()
        {
            _totalInputTokens = 0;
            _totalOutputTokens = 0;
        }
        
        /// <summary>Set the colors for the token counter.</summary>
        public void SetColors(DL.Rgb24 fg, DL.Rgb24 bg, DL.Rgb24 accent)
        {
            _fg = fg;
            _bg = bg;
            _accent = accent;
        }
        
        /// <summary>Render the token counter at the specified position.</summary>
        public void RenderAt(int x, int y, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int totalTokens = _totalInputTokens + _totalOutputTokens;
            string text = $"Total: {_totalInputTokens}→{_totalOutputTokens} ({totalTokens})";
            
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                var color = (ch == '→' || char.IsDigit(ch)) ? _accent : _fg;
                var attrs = char.IsDigit(ch) ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None;
                
                b.DrawText(new DL.TextRun(x + i, y, ch.ToString(), color, _bg, attrs));
            }
        }
        
        /// <summary>Get the width needed to render the counter.</summary>
        public int GetWidth()
        {
            int totalTokens = _totalInputTokens + _totalOutputTokens;
            return $"Total: {_totalInputTokens}→{_totalOutputTokens} ({totalTokens})".Length;
        }
    }
}