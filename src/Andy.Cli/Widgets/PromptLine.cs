using System;
using System.Collections.Generic;
using Andy.Cli.Themes;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>What the composer will do with the line when it is submitted.</summary>
    public enum PromptMode
    {
        /// <summary>The line is a message for the model (or a slash command).</summary>
        Normal,

        /// <summary>
        /// Shell escape (issue #286): the line is a shell command the USER wants to run locally.
        /// Entered by typing <c>!</c> at offset zero on an empty prompt; left with Escape or
        /// Backspace on an empty prompt.
        /// </summary>
        Shell
    }

    /// <summary>
    /// Single-line prompt with editing, history, suggestions, optional caret and border.
    /// Provides a method to compute the terminal cursor position for a thin-bar caret.
    /// </summary>
    public sealed class PromptLine
    {
        /// <summary>The character that switches the composer into shell mode.</summary>
        public const char ShellModeTrigger = '!';

        private string _text = string.Empty;
        private int _cursor;
        private PromptMode _mode = PromptMode.Normal;
        private bool _shellModeAvailable = true;
        private bool _focused;
        private readonly List<string> _history = new();
        private int _historyIndex = -1; // -1 = current editing
        private Func<string, string?>? _suggest;
        private bool _showCaret = true;
        private bool _showBorder = true;
        private bool _useTerminalCursor = true;
        private int _lastX, _lastY, _lastInnerW, _lastStart;
        private int _viewportStart;
        // Wrap width (in columns) used for soft-wrapping the input across visual rows.
        // Set by the host before measuring/rendering so cursor math and height match the
        // actual render width. A non-positive value disables wrapping.
        private int _wrapWidth;
        private DateTime _lastKeyTime = DateTime.MinValue;
        private const int PasteDetectionThresholdMs = 30; // Keys within 30ms are considered a paste
        private bool _inPasteMode = false;
        private DateTime _pasteStartTime = DateTime.MinValue;

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
        /// <summary>
        /// Set the available inner width (in columns) used to soft-wrap the input text.
        /// This must match the width the text is rendered at so that cursor positioning,
        /// vertical navigation and height measurement all agree. A non-positive value
        /// disables wrapping (single logical-line-per-row behavior).
        /// </summary>
        public void SetWrapWidth(int innerWidth) { _wrapWidth = innerWidth; }
        /// <summary>Current prompt text.</summary>
        public string Text => _text;
        /// <summary>
        /// Caret position as a character index into <see cref="Text"/>. Exposed so features that
        /// need cursor-aware behaviour (such as the @-mention picker) can reason about the token
        /// being edited without duplicating the buffer.
        /// </summary>
        public int CursorPosition => _cursor;
        /// <summary>Set the prompt text and move cursor to end.</summary>
        public void SetText(string text)
        {
            _text = text ?? string.Empty;
            _cursor = _text.Length;
        }
        /// <summary>
        /// Replace <paramref name="length"/> characters starting at <paramref name="start"/> with
        /// <paramref name="replacement"/> and place the caret at <paramref name="newCursor"/>
        /// (default: just after the inserted text). Out-of-range arguments are clamped, and the
        /// rest of the buffer - including text on other lines - is left untouched, so a completion
        /// accepted mid-prompt does not disturb the surrounding multiline content.
        /// </summary>
        public void ReplaceRange(int start, int length, string replacement, int? newCursor = null)
        {
            replacement ??= string.Empty;
            start = Math.Clamp(start, 0, _text.Length);
            length = Math.Clamp(length, 0, _text.Length - start);
            _text = _text.Remove(start, length).Insert(start, replacement);
            _cursor = Math.Clamp(newCursor ?? (start + replacement.Length), 0, _text.Length);

        /// <summary>What submitting the current line will do. See <see cref="PromptMode"/>.</summary>
        public PromptMode Mode => _mode;

        /// <summary>
        /// Whether typing <c>!</c> at offset zero can enter shell mode. Set false when the feature
        /// is disabled by configuration, in which case <c>!</c> is an ordinary character again and
        /// the composer can never leave <see cref="PromptMode.Normal"/>.
        /// </summary>
        public void SetShellModeAvailable(bool available)
        {
            _shellModeAvailable = available;
            if (!available) _mode = PromptMode.Normal;
        }

        /// <summary>
        /// Leaves shell mode when the prompt is in it AND empty, returning true when it did.
        ///
        /// The empty-prompt condition is what makes Escape safe to route here first: with text on
        /// the line Escape keeps its usual meaning (the exit dialog), so the key never silently
        /// changes behaviour depending on an invisible mode. Backspace follows the same rule, which
        /// is why deleting back past the start of a shell command drops you into normal mode
        /// instead of doing nothing.
        /// </summary>
        public bool TryExitShellMode()
        {
            if (_mode != PromptMode.Shell || _text.Length != 0) return false;
            _mode = PromptMode.Normal;
            return true;
        }

        /// <summary>
        /// Enters shell mode when it is available and the prompt is empty. Returns true when the
        /// mode changed. Exposed for the command palette and tests; ordinary entry is by typing
        /// <see cref="ShellModeTrigger"/>.
        /// </summary>
        public bool TryEnterShellMode()
        {
            if (!_shellModeAvailable || _mode == PromptMode.Shell || _text.Length != 0) return false;
            _mode = PromptMode.Shell;
            return true;
        }
        /// <summary>Compute the terminal 1-based cursor column and row for the caret, if terminal cursor is enabled.</summary>
        public bool TryGetTerminalCursor(out int col1, out int row1)
        {
            if (!_useTerminalCursor) { col1 = row1 = 0; return false; }
            var (caretRow, caretCol) = GetCaretRowCol();
            int visibleStart = _lastStart; // reused to store start line for multiline
            int innerW = Math.Max(0, _lastInnerW);
            int col = Math.Clamp(caretCol, 0, innerW);
            int borderOffset = _showBorder ? 1 : 0; // Account for top border
            const int promptPrefixWidth = 3; // " > " = 3 characters
            col1 = _lastX + 1 + promptPrefixWidth + col + 1; // 1-based including left margin + prompt prefix
            row1 = _lastY + 1 + borderOffset + Math.Max(0, caretRow - visibleStart);
            return true;
        }

        // Returns submitted line on Enter; otherwise null
        /// <summary>Handle a key press. Ctrl+Enter inserts newline. Returns submitted line on Enter (no Ctrl); otherwise null.</summary>
        public string? OnKey(ConsoleKeyInfo k)
        {
            var now = DateTime.UtcNow;
            var timeSinceLastKey = (now - _lastKeyTime).TotalMilliseconds;

            // Enter paste mode if keys are arriving rapidly
            if (timeSinceLastKey < PasteDetectionThresholdMs && timeSinceLastKey > 0)
            {
                if (!_inPasteMode)
                {
                    _inPasteMode = true;
                    _pasteStartTime = now;
                }
            }

            // Exit paste mode after 500ms of inactivity
            if (_inPasteMode && timeSinceLastKey > 500)
            {
                _inPasteMode = false;
            }

            var isPasting = _inPasteMode;
            _lastKeyTime = now;

            // Shell escape (#286): "!" typed at offset zero on an empty prompt switches the
            // composer to shell mode instead of inserting the character.
            //
            // Two guards keep this from firing when the user did not mean it. The prompt must be
            // EMPTY with the cursor at zero, so a "!" anywhere in a message ("really!") is just a
            // character. And it must not be part of a PASTE: pasting a script or a chat snippet
            // that happens to start with "!" must land as text, not silently arm a shell command.
            if (_shellModeAvailable
                && _mode == PromptMode.Normal
                && !isPasting
                && k.KeyChar == ShellModeTrigger
                && (k.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0
                && _cursor == 0
                && _text.Length == 0)
            {
                _mode = PromptMode.Shell;
                return null;
            }

            // Escape on an empty shell prompt returns to normal mode. The interactive loop routes
            // Escape here first for exactly this case; with text on the line it keeps its usual
            // meaning and never reaches this method.
            if (k.Key == ConsoleKey.Escape)
            {
                TryExitShellMode();
                return null;
            }

            // Ctrl+Enter inserts newline
            if (k.Key == ConsoleKey.Enter && (k.Modifiers & ConsoleModifiers.Control) != 0)
            {
                _text = _text.Insert(_cursor, "\n"); _cursor++; return null;
            }

            // Enter submits ONLY if not pasting
            // During paste, Enter inserts newline instead of submitting
            if (k.Key == ConsoleKey.Enter)
            {
                if (isPasting)
                {
                    // Insert newline when pasting
                    _text = _text.Insert(_cursor, "\n");
                    _cursor++;
                    return null;
                }
                else
                {
                    // Submit when user presses Enter
                    var s = _text; if (!string.IsNullOrWhiteSpace(s)) { _history.Add(s); }
                    _historyIndex = -1; _text = string.Empty; _cursor = 0; return s;
                }
            }

            // Ctrl+A - Move to beginning of current line (emacs-style)
            if (k.Key == ConsoleKey.A && (k.Modifiers & ConsoleModifiers.Control) != 0)
            {
                // Move to start of current line
                while (_cursor > 0 && _text[_cursor - 1] != '\n')
                {
                    _cursor--;
                }
                return null;
            }

            // Ctrl+E - Move to end of current line (emacs-style)
            if (k.Key == ConsoleKey.E && (k.Modifiers & ConsoleModifiers.Control) != 0)
            {
                // Move to end of current line
                while (_cursor < _text.Length && _text[_cursor] != '\n')
                {
                    _cursor++;
                }
                return null;
            }

            // Ctrl+K - Kill line from cursor to end of current line (emacs-style)
            if (k.Key == ConsoleKey.K && (k.Modifiers & ConsoleModifiers.Control) != 0)
            {
                if (_cursor < _text.Length)
                {
                    // Find the end of current line (next newline or end of text)
                    int endOfLine = _cursor;
                    while (endOfLine < _text.Length && _text[endOfLine] != '\n')
                    {
                        endOfLine++;
                    }

                    // Remove from cursor to end of line (but not the newline itself)
                    int lengthToRemove = endOfLine - _cursor;
                    if (lengthToRemove > 0)
                    {
                        _text = _text.Remove(_cursor, lengthToRemove);
                    }
                }
                return null;
            }

            // Ctrl+U - Kill line from beginning of current line to cursor (emacs-style)
            if (k.Key == ConsoleKey.U && (k.Modifiers & ConsoleModifiers.Control) != 0)
            {
                if (_cursor > 0)
                {
                    // Find the start of current line (previous newline or start of text)
                    int startOfLine = _cursor - 1;
                    while (startOfLine > 0 && _text[startOfLine - 1] != '\n')
                    {
                        startOfLine--;
                    }

                    // Remove from start of line to cursor
                    int lengthToRemove = _cursor - startOfLine;
                    if (lengthToRemove > 0)
                    {
                        _text = _text.Remove(startOfLine, lengthToRemove);
                        _cursor = startOfLine;
                    }
                }
                return null;
            }

            // Navigation keys
            if (k.Key == ConsoleKey.LeftArrow) { if (_cursor > 0) _cursor--; return null; }
            if (k.Key == ConsoleKey.RightArrow) { if (_cursor < _text.Length) _cursor++; return null; }

            // Home/End - move to start/end of current line (or whole text if Ctrl is held)
            if (k.Key == ConsoleKey.Home)
            {
                if ((k.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    _cursor = 0; // Ctrl+Home: start of entire text
                }
                else
                {
                    // Move to start of current line
                    while (_cursor > 0 && _text[_cursor - 1] != '\n')
                    {
                        _cursor--;
                    }
                }
                return null;
            }
            if (k.Key == ConsoleKey.End)
            {
                if ((k.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    _cursor = _text.Length; // Ctrl+End: end of entire text
                }
                else
                {
                    // Move to end of current line
                    while (_cursor < _text.Length && _text[_cursor] != '\n')
                    {
                        _cursor++;
                    }
                }
                return null;
            }

            // Up/Down arrows - navigate between lines in multi-line text, or history if single line
            if (k.Key == ConsoleKey.UpArrow)
            {
                if ((k.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    // Ctrl+Up: Navigate history
                    NavigateHistory(-1);
                }
                else
                {
                    // Move cursor up one line
                    MoveCursorVertically(-1);
                }
                return null;
            }
            if (k.Key == ConsoleKey.DownArrow)
            {
                if ((k.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    // Ctrl+Down: Navigate history
                    NavigateHistory(+1);
                }
                else
                {
                    // Move cursor down one line
                    MoveCursorVertically(+1);
                }
                return null;
            }

            // Editing keys
            if (k.Key == ConsoleKey.Backspace)
            {
                // Backspace at the very start of an empty shell prompt deletes the mode itself -
                // the "!" the user typed to get here - rather than doing nothing (#286).
                if (TryExitShellMode()) return null;

                if (_cursor > 0)
                {
                    _text = _text.Remove(_cursor - 1, 1);
                    _cursor--;
                }
                return null;
            }
            if (k.Key == ConsoleKey.Delete) { if (_cursor < _text.Length) { _text = _text.Remove(_cursor, 1); } return null; }

            // Insert regular characters (including pasted newlines)
            if (!char.IsControl(k.KeyChar) || k.KeyChar == '\n' || k.KeyChar == '\r')
            {
                var ch = k.KeyChar;

                // When pasting, handle line endings specially to support CRLF
                if (ch == '\r')
                {
                    // Always insert newline for \r
                    _text = _text.Insert(_cursor, "\n");
                    _cursor++;
                    return null;
                }
                else if (ch == '\n')
                {
                    // Check if the previous character was a newline (from \r in CRLF)
                    // If so, skip this \n to avoid double newlines
                    if (_cursor > 0 && _text[_cursor - 1] == '\n')
                    {
                        // Previous char was already a newline from \r, skip this \n
                        return null;
                    }
                    else
                    {
                        // Standalone \n, insert it
                        _text = _text.Insert(_cursor, "\n");
                        _cursor++;
                        return null;
                    }
                }

                // Filter out any escape sequences or control codes that might slip through
                // but preserve printable characters and tabs
                if (ch >= 32 || ch == '\t')
                {
                    _text = _text.Insert(_cursor, ch.ToString());
                    _cursor++;
                }
                return null;
            }
            return null;
        }

        /// <summary>Handle pasted text directly (used for bracketed paste mode support)</summary>
        public void InsertText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Normalize line endings
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            _text = _text.Insert(_cursor, text);
            _cursor += text.Length;
        }

        private void NavigateHistory(int delta)
        {
            if (_history.Count == 0) return;
            if (_historyIndex == -1) _historyIndex = _history.Count; // virtual current row
            _historyIndex = Math.Max(0, Math.Min(_history.Count, _historyIndex + delta));
            if (_historyIndex >= 0 && _historyIndex < _history.Count) { _text = _history[_historyIndex]; _cursor = _text.Length; }
            else { _historyIndex = -1; _text = string.Empty; _cursor = 0; }
        }

        private void MoveCursorVertically(int direction)
        {
            // direction: -1 for up, +1 for down. Operates on VISUAL rows so that moving up/down
            // walks across soft-wrapped rows, not just explicit newlines.
            if (string.IsNullOrEmpty(_text)) return;

            var rows = CurrentVisualRows();
            var (currentRow, currentCol) = GetCaretRowCol();

            int targetRow = currentRow + direction;
            if (targetRow < 0 || targetRow >= rows.Count) return;

            var target = rows[targetRow];
            // Try to keep the same column, clamped to the target row's content length.
            int newCursor = target.Start + Math.Min(currentCol, target.Length);
            _cursor = Math.Clamp(newCursor, 0, Normalize(_text).Length);
        }

        /// <summary>Render the prompt within the provided rectangle.</summary>
        /// <param name="themeOverride">
        /// Theme to render with; defaults to <see cref="Theme.Current"/>. Passing an
        /// explicit theme keeps rendering deterministic (e.g. in tests) regardless of
        /// the global active theme.
        /// </param>
        public void Render(in L.Rect rect, DL.DisplayList baseDl, DL.DisplayListBuilder b, Theme? themeOverride = null)
        {
            var theme = themeOverride ?? Theme.Current;
            int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;
            const int promptPrefixWidth = 3; // " > " = 3 characters
            int innerW = Math.Max(0, w - 2 - promptPrefixWidth); // Account for borders + prompt prefix
            _lastX = x; _lastY = y; _lastInnerW = innerW;
            // Wrap at the actual render width so measurement and cursor math stay consistent.
            _wrapWidth = innerW;
            b.PushClip(new DL.ClipPush(x, y, w, h));
            // background and optional border
            b.DrawRect(new DL.Rect(x, y, w, h, theme.PromptBackground));
            // Shell mode is deliberately loud (#286): the border and the prompt glyph both change,
            // because the cost of not noticing which mode you are in is running a shell command you
            // meant to send to the model. Two independent signals, so a theme that dims one still
            // leaves the other.
            bool shellMode = _mode == PromptMode.Shell;
            var borderColor = shellMode ? theme.Warning : theme.Border;
            if (_showBorder) b.DrawBorder(new DL.Border(x, y, w, h, "single", borderColor));
            // Visual rows after soft-wrapping; caret placement uses the same rows.
            string norm = Normalize(_text);
            var rows = ComputeVisualRows(norm, innerW);
            int total = rows.Count;
            int maxContentLines = _showBorder ? Math.Max(1, h - 2) : h;
            int visible = Math.Min(maxContentLines, total);
            // Keep the caret's visual row in view. Previously an overflowing prompt always
            // rendered its final rows, so UpArrow moved the logical cursor off-screen and
            // appeared not to work. Preserve the current viewport until the caret crosses an
            // edge, then scroll only enough to reveal it.
            var (caretRow, _) = GetCaretRowCol();
            int maxStart = Math.Max(0, total - visible);
            int startLine = Math.Clamp(_viewportStart, 0, maxStart);
            if (caretRow < startLine)
            {
                startLine = caretRow;
            }
            else if (caretRow >= startLine + visible)
            {
                startLine = caretRow - visible + 1;
            }
            _viewportStart = Math.Clamp(startLine, 0, maxStart);
            startLine = _viewportStart;
            _lastStart = startLine; // reuse as start row for cursor calc
            int textStartY = _showBorder ? y + 1 : y; // Start after top border
            int textStartX = x + 1 + promptPrefixWidth; // Start after border + prompt prefix
            for (int i = 0; i < visible; i++)
            {
                // Draw prompt prefix " > " on first visible row
                if (i == 0)
                {
                    var glyph = shellMode ? ShellModeTrigger.ToString() : ">";
                    var glyphColor = shellMode ? theme.Warning : theme.Accent;
                    b.DrawText(new DL.TextRun(x + 1, textStartY + i, " ", theme.Primary, theme.PromptBackground, DL.CellAttrFlags.None));
                    b.DrawText(new DL.TextRun(x + 2, textStartY + i, glyph, glyphColor, theme.PromptBackground, DL.CellAttrFlags.Bold));
                    b.DrawText(new DL.TextRun(x + 3, textStartY + i, " ", theme.Primary, theme.PromptBackground, DL.CellAttrFlags.None));
                }
                var row = rows[startLine + i];
                string snippet = norm.Substring(row.Start, row.Length);
                b.DrawText(new DL.TextRun(textStartX, textStartY + i, snippet, theme.PromptText, theme.PromptBackground, DL.CellAttrFlags.None));
            }
            // Shell mode with nothing typed yet explains itself, so the changed border and glyph
            // are not the only clue about what Enter is about to do.
            if (shellMode && norm.Length == 0 && visible > 0)
            {
                const string hint = "shell command - Esc to cancel";
                var trimmed = hint.Length > innerW ? hint[..Math.Max(0, innerW)] : hint;
                if (trimmed.Length > 0)
                {
                    b.DrawText(new DL.TextRun(textStartX, textStartY, trimmed, theme.Ghost, theme.PromptBackground, DL.CellAttrFlags.None));
                }
            }

            // ghost suggestion only on last visible row
            if (_suggest is not null && _focused && visible > 0)
            {
                var sug = _suggest(_text);
                if (!string.IsNullOrEmpty(sug))
                {
                    int lastRow = textStartY + visible - 1;
                    var lastVisual = rows[startLine + visible - 1];
                    int lastLen = lastVisual.Length;
                    int room = Math.Max(0, innerW - Math.Min(innerW, lastLen));
                    string ghost = sug!;
                    if (ghost.Length > room) ghost = ghost.Substring(0, room);
                    b.DrawText(new DL.TextRun(textStartX + Math.Min(innerW, lastLen), lastRow, ghost, theme.Ghost, theme.PromptBackground, DL.CellAttrFlags.None));
                }
            }
            // caret glyph if not using terminal cursor
            if (_showCaret && !_useTerminalCursor)
            {
                var (cr, cc) = GetCaretRowCol();
                int rowInViewport = cr - startLine;
                if (rowInViewport >= 0 && rowInViewport < maxContentLines)
                {
                    int caretCol = Math.Clamp(cc, 0, innerW - 1);
                    b.DrawText(new DL.TextRun(textStartX + caretCol, textStartY + rowInViewport, "|", theme.PromptText, theme.PromptBackground, DL.CellAttrFlags.None));
                }
            }
            // scrollbar when content exceeds max visible lines
            if (_showBorder && total > 7)
            {
                int scrollbarX = x + w - 2;
                int scrollbarHeight = maxContentLines;
                int thumbSize = Math.Max(1, (maxContentLines * maxContentLines) / total);
                int thumbPosition = (startLine * scrollbarHeight) / total;

                for (int i = 0; i < scrollbarHeight; i++)
                {
                    int scrollY = textStartY + i;
                    bool isThumb = i >= thumbPosition && i < thumbPosition + thumbSize;
                    var scrollChar = isThumb ? "█" : "│";
                    b.DrawText(new DL.TextRun(scrollbarX, scrollY, scrollChar, theme.Border, theme.PromptBackground, DL.CellAttrFlags.None));
                }
            }
            b.Pop();
        }

        /// <summary>Return how many visual rows are present (after soft-wrapping at the configured width).</summary>
        public int GetLineCount() => _text.Length == 0 ? 1 : CurrentVisualRows().Count;

        /// <summary>Get the desired height for the prompt area based on content (3-9 lines: 1 top border + 1-7 content + 1 bottom border).</summary>
        public int GetDesiredHeight()
        {
            int contentLines = GetLineCount();
            int clampedContent = Math.Clamp(contentLines, 1, 7);
            return clampedContent + 2; // +2 for top and bottom borders
        }

        /// <summary>
        /// A single visual (possibly soft-wrapped) row. <see cref="Start"/> is the index into the
        /// normalized text where the row begins; <see cref="Length"/> is the number of text
        /// characters shown on the row (excluding any trailing newline). <see cref="EndsWithNewline"/>
        /// is true when the row is terminated by an explicit newline rather than a soft wrap.
        /// </summary>
        private readonly struct VisualRow
        {
            public VisualRow(int start, int length, bool endsWithNewline)
            {
                Start = start;
                Length = length;
                EndsWithNewline = endsWithNewline;
            }
            public int Start { get; }
            public int Length { get; }
            public bool EndsWithNewline { get; }
        }

        /// <summary>Normalize CRLF/CR to LF for consistent wrapping and cursor math.</summary>
        private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>
        /// Compute the visual rows for the given normalized text at a wrap width. Each logical line
        /// (split on '\n') is broken into chunks of at most <paramref name="wrapWidth"/> columns.
        /// A wrap width of zero or less disables wrapping (one visual row per logical line).
        /// Empty logical lines still produce a single (empty) visual row.
        /// </summary>
        private static List<VisualRow> ComputeVisualRows(string text, int wrapWidth)
        {
            var rows = new List<VisualRow>();
            int n = text.Length;
            int lineStart = 0;
            while (true)
            {
                // Find end of this logical line (exclusive) and whether it ends in a newline.
                int lineEnd = lineStart;
                while (lineEnd < n && text[lineEnd] != '\n') lineEnd++;
                bool hasNewline = lineEnd < n; // a '\n' terminates this line
                int lineLen = lineEnd - lineStart;

                if (wrapWidth <= 0 || lineLen <= wrapWidth)
                {
                    rows.Add(new VisualRow(lineStart, lineLen, hasNewline));
                }
                else
                {
                    int pos = lineStart;
                    while (pos < lineEnd)
                    {
                        int chunk = Math.Min(wrapWidth, lineEnd - pos);
                        bool isLastChunk = pos + chunk >= lineEnd;
                        rows.Add(new VisualRow(pos, chunk, isLastChunk && hasNewline));
                        pos += chunk;
                    }
                }

                if (!hasNewline)
                {
                    break;
                }
                lineStart = lineEnd + 1; // skip the '\n'
                // A trailing newline at end of text yields a final empty row.
                if (lineStart > n) break;
                if (lineStart == n)
                {
                    rows.Add(new VisualRow(n, 0, false));
                    break;
                }
            }
            if (rows.Count == 0) rows.Add(new VisualRow(0, 0, false));
            return rows;
        }

        /// <summary>Visual rows for the current text using the configured wrap width.</summary>
        private List<VisualRow> CurrentVisualRows() => ComputeVisualRows(Normalize(_text), _wrapWidth);

        /// <summary>
        /// Map the current cursor index to a (row, col) coordinate in visual space.
        /// A cursor that lands exactly on a soft-wrap boundary is reported at column 0 of the
        /// following row so the caret remains visible instead of sitting in the clipped column.
        /// </summary>
        private (int Row, int Col) GetCaretRowCol()
        {
            var rows = CurrentVisualRows();
            int cursor = Math.Clamp(_cursor, 0, Normalize(_text).Length);
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                int rowEnd = row.Start + row.Length; // index just past the last visible char
                // Cursor strictly inside the row, or at its start.
                if (cursor >= row.Start && cursor < rowEnd)
                {
                    return (r, cursor - row.Start);
                }
                if (cursor == rowEnd)
                {
                    // At the end of this row's content.
                    if (row.EndsWithNewline)
                    {
                        // The newline is consumed by this row; caret sits past the text on this row.
                        return (r, row.Length);
                    }
                    bool isLastRow = r == rows.Count - 1;
                    if (isLastRow)
                    {
                        // End of all text.
                        return (r, row.Length);
                    }
                    // Soft-wrap boundary: prefer column 0 of the next row.
                    return (r + 1, 0);
                }
            }
            // Fallback: end of last row.
            int last = rows.Count - 1;
            return (last, rows[last].Length);
        }
    }
}
