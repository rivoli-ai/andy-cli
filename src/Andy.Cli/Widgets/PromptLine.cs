using System;
using System.Collections.Generic;
using Andy.Cli.Themes;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Single-line prompt with editing, history, suggestions, optional caret and border.
    /// Provides a method to compute the terminal cursor position for a thin-bar caret.
    /// </summary>
    public sealed class PromptLine
    {
        private string _text = string.Empty;
        private int _cursor;
        private bool _focused;
        private readonly List<string> _history = new();
        private int _historyIndex = -1; // -1 = current editing
        private Func<string, string?>? _suggest;
        private bool _showCaret = true;
        private bool _showBorder = true;
        private bool _useTerminalCursor = true;
        private int _lastX, _lastY, _lastInnerW, _lastStart;

        /// <summary>Provide a suggestion function for ghost text.</summary>
        public void SetSuggestionProvider(Func<string, string?>? provider) => _suggest = provider;
        /// <summary>Enable border display.</summary>
        public void SetBorder(bool show) { _showBorder = show; }
        /// <summary>Show or hide the caret; when terminal cursor is used, the caret character is not drawn.</summary>
        public void SetShowCaret(bool show) { _showCaret = show; }
        /// <summary>Set focus state; used for rendering and suggestion visibility.</summary>
        public void SetFocused(bool focused) { _focused = focused; }
        /// <summary>Enable using the terminal cursor for the caret (thin bar) instead of drawing a '|' glyph.</summary>
        public void UseTerminalCursor(bool use) { _useTerminalCursor = use; }
        /// <summary>Current prompt text.</summary>
        public string Text => _text;
        /// <summary>Compute the terminal 1-based cursor column and row for the caret, if terminal cursor is enabled.</summary>
        public bool TryGetTerminalCursor(out int col1, out int row1)
        {
            if (!_useTerminalCursor) { col1 = row1 = 0; return false; }
            var (caretRow, caretCol) = GetCaretRowCol();
            int visibleStart = _lastStart; // reused to store start line for multiline
            int innerW = Math.Max(0, _lastInnerW);
            int col = Math.Clamp(caretCol, 0, innerW);
            col1 = _lastX + 1 + col + 1; // 1-based including left margin
            row1 = _lastY + 1 + Math.Max(0, caretRow - visibleStart);
            return true;
        }

        // Returns submitted line on Enter; otherwise null
        /// <summary>Handle a key press. Ctrl+Enter inserts newline. Returns submitted line on Enter (no Ctrl); otherwise null.</summary>
        public string? OnKey(ConsoleKeyInfo k)
        {
            if (k.Key == ConsoleKey.Enter && (k.Modifiers & ConsoleModifiers.Control) != 0)
            {
                _text = _text.Insert(_cursor, "\n"); _cursor++; return null;
            }
            if (k.Key == ConsoleKey.Enter)
            {
                var s = _text; if (!string.IsNullOrWhiteSpace(s)) { _history.Add(s); }
                _historyIndex = -1; _text = string.Empty; _cursor = 0; return s;
            }
            if (k.Key == ConsoleKey.LeftArrow) { if (_cursor > 0) _cursor--; return null; }
            if (k.Key == ConsoleKey.RightArrow) { if (_cursor < _text.Length) _cursor++; return null; }
            if (k.Key == ConsoleKey.Backspace) { if (_cursor > 0) { bool wasNewline = _text[_cursor - 1] == '\n'; _text = _text.Remove(_cursor - 1, 1); _cursor--; /* shrink happens via GetLineCount */ } return null; }
            if (k.Key == ConsoleKey.Delete) { if (_cursor < _text.Length) { _text = _text.Remove(_cursor, 1); } return null; }
            if (k.Key == ConsoleKey.Home) { _cursor = 0; return null; }
            if (k.Key == ConsoleKey.End) { _cursor = _text.Length; return null; }
            if (k.Key == ConsoleKey.UpArrow) { NavigateHistory(-1); return null; }
            if (k.Key == ConsoleKey.DownArrow) { NavigateHistory(+1); return null; }
            if (!char.IsControl(k.KeyChar))
            {
                var ch = k.KeyChar;
                _text = _text.Insert(_cursor, ch.ToString());
                _cursor++;
                return null;
            }
            return null;
        }

        private void NavigateHistory(int delta)
        {
            if (_history.Count == 0) return;
            if (_historyIndex == -1) _historyIndex = _history.Count; // virtual current row
            _historyIndex = Math.Max(0, Math.Min(_history.Count, _historyIndex + delta));
            if (_historyIndex >= 0 && _historyIndex < _history.Count) { _text = _history[_historyIndex]; _cursor = _text.Length; }
            else { _historyIndex = -1; _text = string.Empty; _cursor = 0; }
        }

        /// <summary>Render the prompt within the provided rectangle.</summary>
        public void Render(in L.Rect rect, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            var theme = Theme.Current;
            int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;
            int innerW = Math.Max(0, w - 2);
            _lastX = x; _lastY = y; _lastInnerW = innerW;
            b.PushClip(new DL.ClipPush(x, y, w, h));
            // background and optional border
            b.DrawRect(new DL.Rect(x, y, w, h, theme.PromptBackground));
            if (_showBorder) b.DrawBorder(new DL.Border(x, y, w, h, "single", theme.Border));
            // Lines and caret placement
            var lines = _text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int total = lines.Length;
            int visible = Math.Min(h, total);
            int startLine = Math.Max(0, total - visible);
            _lastStart = startLine; // reuse as start line for cursor calc
            for (int i = 0; i < visible; i++)
            {
                string line = lines[startLine + i];
                string snippet = line.Length > innerW ? line.Substring(0, innerW) : line;
                b.DrawText(new DL.TextRun(x + 1, y + i, snippet, theme.Primary, theme.PromptBackground, DL.CellAttrFlags.None));
            }
            // ghost suggestion only on last visible row
            if (_suggest is not null && _focused && visible > 0)
            {
                var sug = _suggest(_text);
                if (!string.IsNullOrEmpty(sug))
                {
                    int lastRow = y + visible - 1;
                    string lastLine = lines.Length > 0 ? lines[^1] : string.Empty;
                    int room = Math.Max(0, innerW - Math.Min(innerW, lastLine.Length));
                    string ghost = sug!;
                    if (ghost.Length > room) ghost = ghost.Substring(0, room);
                    b.DrawText(new DL.TextRun(x + 1 + Math.Min(innerW, lastLine.Length), lastRow, ghost, theme.Ghost, theme.PromptBackground, DL.CellAttrFlags.None));
                }
            }
            // caret glyph if not using terminal cursor
            if (_showCaret && !_useTerminalCursor)
            {
                var (cr, cc) = GetCaretRowCol();
                int rowInViewport = cr - startLine;
                if (rowInViewport >= 0 && rowInViewport < h)
                {
                    int caretCol = Math.Clamp(cc, 0, innerW - 1);
                    b.DrawText(new DL.TextRun(x + 1 + caretCol, y + rowInViewport, "|", theme.Primary, theme.PromptBackground, DL.CellAttrFlags.None));
                }
            }
            b.Pop();
        }

        /// <summary>Return how many visual lines are present (splitting on newlines).</summary>
        public int GetLineCount() => _text.Length == 0 ? 1 : _text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length;

        private (int Row, int Col) GetCaretRowCol()
        {
            // Count newlines before cursor
            int row = 0, col = 0;
            for (int i = 0; i < _cursor && i < _text.Length; i++)
            {
                if (_text[i] == '\n') { row++; col = 0; }
                else col++;
            }
            return (row, col);
        }
    }
}
