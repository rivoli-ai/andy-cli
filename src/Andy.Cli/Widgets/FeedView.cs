using System;
using System.Collections.Generic;
using System.Linq;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Scrollable stack of feed items (markdown, code blocks, tools, etc.).
    /// Supports bottom-follow, manual scrolling, and simple scroll-in animation for newly appended content.
    /// </summary>
    public sealed class FeedView
    {
        private readonly List<IFeedItem> _items = new();
        private readonly object _itemsLock = new(); // Thread-safety for _items collection
        private int _scrollOffset; // lines from bottom; 0 = bottom
        private bool _followTail = true;
        private bool _focused;
        private int _prevTotalLines;
        private int _animRemaining; // lines to animate in
        private int _animSpeed = 2; // lines per frame

        /// <summary>When true and scrolled to bottom, keep content pinned to bottom.</summary>
        public bool FollowTail { get => _followTail; set => _followTail = value; }
        /// <summary>Set focus state for the feed to affect rendering.</summary>
        public void SetFocused(bool focused) { _focused = focused; }
        /// <summary>Set animation speed in lines per frame.</summary>
        public void SetAnimationSpeed(int linesPerFrame) { _animSpeed = Math.Max(0, linesPerFrame); }
        /// <summary>Append a new item to the feed.</summary>
        public void AddItem(IFeedItem item)
        {
            if (item is not null)
            {
                lock (_itemsLock)
                {
                    _items.Add(item);
                }
            }
        }
        /// <summary>Convenience: append markdown item.</summary>
        public void AddMarkdown(string md) => AddItem(new MarkdownItem(md));
        /// <summary>Convenience: append markdown using Andy.Tui.Widgets.MarkdownRenderer to better handle inline formatting.</summary>
        public void AddMarkdownRich(string md) => AddItem(new MarkdownRendererItem(md));
        /// <summary>Convenience: append code block item.</summary>
        public void AddCode(string code, string? language = null) => AddItem(new CodeBlockItem(code, language));
        /// <summary>Append a user message bubble with a rounded frame and label.</summary>
        public void AddUserMessage(string text)
        {
            // Add spacing before user messages for better readability
            AddItem(new SpacerItem(1));
            AddItem(new UserBubbleItem(text));
        }
        /// <summary>Append a response separator with token information.</summary>
        public void AddResponseSeparator(int inputTokens = 0, int outputTokens = 0, string pattern = "━━ ◆ ━━") => AddItem(new ResponseSeparatorItem(inputTokens, outputTokens, pattern));

        /// <summary>Add a streaming message that can be updated progressively.</summary>
        public StreamingMessageItem AddStreamingMessage()
        {
            var item = new StreamingMessageItem();
            AddItem(item);
            return item;
        }

        /// <summary>Add a tool execution display with dotted yellow line.</summary>
        public void AddToolExecution(string toolId, Dictionary<string, object?> parameters, string? result = null, bool isSuccess = true)
        {
            AddItem(new ToolExecutionItem(toolId, parameters, result, isSuccess));
            // Add a blank line after tool output for better readability
            AddItem(new SpacerItem(1));
        }

        /// <summary>Expose a snapshot of items for verification in tests.</summary>
        internal IReadOnlyList<IFeedItem> GetItemsForTesting()
        {
            return _items.ToList();
        }

        /// <summary>Clear all items from the feed.</summary>
        public void Clear()
        {
            _items.Clear();
            _scrollOffset = 0;
            _followTail = true;
            _prevTotalLines = 0;
            _animRemaining = 0;
            _totalLinesCache = 0;
        }

        /// <summary>Scroll the feed by delta lines (positive = up). Returns current offset.</summary>
        public int ScrollLines(int delta, int pageSize)
        {
            int total = _totalLinesCache;
            if (total <= 0) return _scrollOffset;
            if (delta == int.MaxValue) delta = pageSize;
            if (delta == int.MinValue) delta = -pageSize;
            _scrollOffset = Math.Max(0, _scrollOffset + delta);
            _followTail = _scrollOffset == 0;
            return _scrollOffset;
        }

        /// <summary>Advance animation state one frame.</summary>
        public void Tick()
        {
            if (_animRemaining > 0) _animRemaining = Math.Max(0, _animRemaining - _animSpeed);
        }

        private int _totalLinesCache;

        /// <summary>Render feed items inside rect, stacking vertically with bottom alignment when following tail.</summary>
        public void Render(in L.Rect rect, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;

            // Guard against invalid dimensions to prevent crashes
            if (w <= 0 || h <= 0) return;

            b.PushClip(new DL.ClipPush(x, y, w, h));
            var bg = new DL.Rgb24(0, 0, 0);
            b.DrawRect(new DL.Rect(x, y, w, h, bg));
            // Focus indicator on left margin
            if (_focused)
            {
                var bar = new DL.Rgb24(60, 60, 30);
                b.DrawRect(new DL.Rect(x, y, 1, h, bar));
            }

            // Thread-safe snapshot of items for rendering
            IFeedItem[] itemsSnapshot;
            lock (_itemsLock)
            {
                itemsSnapshot = _items.ToArray();
            }

            // Measure all items at current width
            var lineCounts = new int[itemsSnapshot.Length];
            int total = 0;
            for (int i = 0; i < itemsSnapshot.Length; i++) { int lc = itemsSnapshot[i].MeasureLineCount(w - 2); lineCounts[i] = lc; total += lc; }

            // Update animation on growth
            if (_followTail && _scrollOffset == 0 && total > _prevTotalLines)
            {
                _animRemaining = Math.Min(_animRemaining + (total - _prevTotalLines), total);
            }
            _prevTotalLines = total; _totalLinesCache = total;

            int visible = Math.Min(h, total);
            int startLine;
            if (_followTail && _scrollOffset == 0)
            {
                int baseStart = Math.Max(0, total - visible);
                startLine = Math.Max(0, baseStart - _animRemaining);
            }
            else
            {
                startLine = Math.Max(0, total - visible - _scrollOffset);
            }
            int drawn = 0;
            int cy = y + Math.Max(0, h - Math.Min(visible, total - startLine)); // bottom align

            // Walk items and render slices
            int cursor = 0; // line cursor into content
            for (int i = 0; i < itemsSnapshot.Length && drawn < h; i++)
            {
                int itemLines = lineCounts[i];
                int itemStart = cursor;
                int itemEnd = cursor + itemLines;
                cursor = itemEnd;
                if (itemEnd <= startLine) continue; // before viewport
                if (itemStart >= startLine + h) break; // after viewport
                int sliceStart = Math.Max(0, startLine - itemStart);
                int maxLines = Math.Min(itemLines - sliceStart, (startLine + h) - Math.Max(startLine, itemStart));

                // Critical fix: Ensure we never render beyond the allocated height
                maxLines = Math.Min(maxLines, h - drawn);
                if (maxLines <= 0) continue;

                // Additional safety: ensure cy is within the allocated region
                if (cy + maxLines > y + h)
                {
                    maxLines = Math.Max(0, (y + h) - cy);
                    if (maxLines <= 0) break;
                }

                itemsSnapshot[i].RenderSlice(x + 1, cy, w - 2, sliceStart, maxLines, baseDl, b);
                cy += maxLines;
                drawn += maxLines;
            }

            b.Pop();
        }
    }

    /// <summary>Contract for a line-oriented feed item that can render any slice of its lines.</summary>
    public interface IFeedItem
    {
        /// <summary>Measure how many lines this item would occupy at a given width.</summary>
        int MeasureLineCount(int width);
        /// <summary>Render a slice of this item: starting at a line offset, for up to maxLines.
        /// Implementations should clip horizontally to width and not draw outside the provided region.
        /// </summary>
        void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b);
    }

    /// <summary>Markdown feed item using a naive line-by-line renderer with fenced code detection.</summary>
    public sealed class MarkdownItem : IFeedItem
    {
        private readonly string[] _lines;
        /// <summary>Create a markdown item from raw markdown text.</summary>
        public MarkdownItem(string markdown)
        {
            _lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
        /// <inheritdoc />
        public int MeasureLineCount(int width)
        {
            // No wrapping for now; one input line -> one row on screen
            return _lines.Length;
        }
        /// <inheritdoc />
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            bool inCode = false;
            int printed = 0;
            for (int i = startLine; i < _lines.Length && printed < maxLines; i++)
            {
                var line = _lines[i];
                if (line.StartsWith("```")) { inCode = !inCode; continue; }
                DL.Rgb24 fg;
                DL.CellAttrFlags attr = DL.CellAttrFlags.None;
                if (!inCode && line.StartsWith("# ")) { line = line.Substring(2); fg = new DL.Rgb24(100, 200, 255); attr = DL.CellAttrFlags.Bold; }
                else if (!inCode && line.StartsWith("## ")) { line = line.Substring(3); fg = new DL.Rgb24(150, 220, 150); attr = DL.CellAttrFlags.Bold; }
                else if (!inCode && line.StartsWith("### ")) { line = line.Substring(4); fg = new DL.Rgb24(255, 180, 100); attr = DL.CellAttrFlags.Bold; }
                else if (inCode) { fg = new DL.Rgb24(180, 180, 180); }
                else { fg = new DL.Rgb24(220, 220, 220); }
                string t = line.Length > width ? line.Substring(0, width) : line;
                b.DrawText(new DL.TextRun(x, y + printed, t, fg, new DL.Rgb24(0, 0, 0), attr));
                printed++;
            }
        }
    }

    /// <summary>Markdown feed item that uses Andy.Tui.Widgets.MarkdownRenderer for improved inline formatting.</summary>
    public sealed class MarkdownRendererItem : IFeedItem
    {
        private readonly string _md;
        private readonly string _originalMd;
        public MarkdownRendererItem(string markdown)
        {
            _originalMd = markdown ?? string.Empty;
            // Preprocess markdown to prevent "You" from being highlighted
            // The Andy.Tui markdown renderer seems to treat "You" as a special keyword
            // We'll insert a zero-width non-joiner to break the word without affecting display
            _md = _originalMd;
            // Replace standalone "You" but not "You:" in user prompts
            _md = System.Text.RegularExpressions.Regex.Replace(_md, @"\bYou\b(?!:)", "Y\u200Cou");
        }
        public int MeasureLineCount(int width)
        {
            // Calculate actual line count considering word wrapping
            if (width <= 0) return 1;

            var lines = _md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int totalLines = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    totalLines++;
                }
                else
                {
                    // Account for word wrapping - estimate based on line length
                    // Markdown renderer typically uses about 80% of width for content
                    int effectiveWidth = Math.Max(1, (int)(width * 0.8));
                    int wrappedLines = (line.Length + effectiveWidth - 1) / effectiveWidth;
                    totalLines += Math.Max(1, wrappedLines);
                }
            }

            return Math.Max(1, totalLines);
        }
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            // Guard against invalid dimensions
            if (width <= 0 || maxLines <= 0) return;

            // Render only the requested slice by extracting those lines
            var lines = _md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // Guard against invalid startLine
            if (startLine >= lines.Length || startLine < 0) return;

            int end = Math.Min(lines.Length, startLine + maxLines);
            var slice = string.Join("\n", lines[startLine..end]);
            // Detect simple HTML links <a href="...">text</a> and render with Link widget
            if (TryRenderSimpleHtmlLink(slice, x, y, width, maxLines, baseDl, b)) return;
            var r = new Andy.Tui.Widgets.MarkdownRenderer();
            r.SetText(slice);
            r.Render(new L.Rect(x, y, width, maxLines), baseDl, b);
        }

        private static bool TryRenderSimpleHtmlLink(string text, int x, int y, int width, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            // Very naive detection for a single-line anchor
            // <a href="URL">TEXT</a>
            var m = System.Text.RegularExpressions.Regex.Match(text.Trim(), "^<a\\s+href=\"([^\"]+)\">([^<]+)</a>$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            string url = m.Groups[1].Value;
            string label = m.Groups[2].Value;
            var link = new Andy.Tui.Widgets.Link();
            link.SetUrl(url);
            link.SetText(label);
            link.EnableOsc8(true);
            link.Render(new L.Rect(x, y, Math.Max(1, width), 1), baseDl, b);
            return true;
        }
    }

    /// <summary>Code block feed item with shaded background.</summary>
    public sealed class CodeBlockItem : IFeedItem
    {
        private readonly string[] _lines;
        private readonly string? _lang;
        /// <summary>Create a code block item from source text and optional language tag.</summary>
        public CodeBlockItem(string code, string? language = null)
        { _lines = (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); _lang = language; }
        internal string? GetLanguageForTesting() => _lang;
        /// <inheritdoc />
        public int MeasureLineCount(int width)
        {
            const int lineNumWidth = 4; // "999 " (3 digits + space)
            int contentWidth = Math.Max(1, width - lineNumWidth);
            int totalVisualLines = 0;

            foreach (var line in _lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    totalVisualLines++;
                }
                else
                {
                    // Calculate how many visual lines this logical line will take
                    int wrappedLines = Math.Max(1, (int)Math.Ceiling((double)line.Length / contentWidth));
                    totalVisualLines += wrappedLines;
                }
            }

            return totalVisualLines;
        }
        /// <inheritdoc />
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            var bg = new DL.Rgb24(20, 20, 30);
            var fg = new DL.Rgb24(200, 200, 220);
            var lineNumColor = new DL.Rgb24(120, 140, 160); // Subtle blue-gray for line numbers
            var lineNumSeparatorColor = new DL.Rgb24(80, 90, 100); // Darker separator

            const int lineNumWidth = 4; // "999 " (3 digits + space)
            int contentX = x + lineNumWidth;
            int contentWidth = Math.Max(1, width - lineNumWidth);

            // background block (includes line number area)
            b.PushClip(new DL.ClipPush(x - 1, y, width + 2, maxLines));
            b.DrawRect(new DL.Rect(x - 1, y, width + 2, maxLines, bg));

            int currentVisualLine = 0;

            // Find which logical line corresponds to startLine
            int logicalLineIndex = 0;
            int visualLineOffset = 0;

            for (int logLine = 0; logLine < _lines.Length && currentVisualLine < startLine; logLine++)
            {
                string line = _lines[logLine];
                int wrappedLines = string.IsNullOrEmpty(line) ? 1 : Math.Max(1, (int)Math.Ceiling((double)line.Length / contentWidth));

                if (currentVisualLine + wrappedLines > startLine)
                {
                    logicalLineIndex = logLine;
                    visualLineOffset = startLine - currentVisualLine;
                    break;
                }
                currentVisualLine += wrappedLines;
                logicalLineIndex = logLine + 1;
            }

            // Render the visible portion
            int renderedLines = 0;
            for (int logLine = logicalLineIndex; logLine < _lines.Length && renderedLines < maxLines; logLine++)
            {
                string line = _lines[logLine];
                int lineNumber = logLine + 1;

                if (string.IsNullOrEmpty(line))
                {
                    // Empty line
                    if (logLine == logicalLineIndex && visualLineOffset > 0) continue;

                    string lineNumText = lineNumber.ToString().PadLeft(3);
                    b.DrawText(new DL.TextRun(x, y + renderedLines, lineNumText, lineNumColor, bg, DL.CellAttrFlags.None));
                    b.DrawText(new DL.TextRun(x + 3, y + renderedLines, " ", lineNumSeparatorColor, bg, DL.CellAttrFlags.None));
                    renderedLines++;
                }
                else
                {
                    // Handle wrapped lines
                    int startOffset = (logLine == logicalLineIndex) ? visualLineOffset * contentWidth : 0;

                    for (int wrapIndex = (logLine == logicalLineIndex ? visualLineOffset : 0);
                         startOffset < line.Length && renderedLines < maxLines;
                         wrapIndex++)
                    {
                        int segmentLength = Math.Min(contentWidth, line.Length - startOffset);
                        string lineSegment = line.Substring(startOffset, segmentLength);

                        // Show line number only for the first visual line of each logical line
                        if (wrapIndex == 0)
                        {
                            string lineNumText = lineNumber.ToString().PadLeft(3);
                            b.DrawText(new DL.TextRun(x, y + renderedLines, lineNumText, lineNumColor, bg, DL.CellAttrFlags.None));
                        }
                        else
                        {
                            // Blank space for continuation lines
                            b.DrawText(new DL.TextRun(x, y + renderedLines, "   ", lineNumColor, bg, DL.CellAttrFlags.None));
                        }

                        b.DrawText(new DL.TextRun(x + 3, y + renderedLines, " ", lineNumSeparatorColor, bg, DL.CellAttrFlags.None));

                        // Render code content with syntax highlighting
                        int cx = contentX;
                        foreach (var (seg, color, attr) in Highlight(lineSegment, _lang))
                        {
                            if (cx >= contentX + contentWidth) break;
                            string t = seg;
                            if (t.Length > (contentX + contentWidth - cx)) t = t.Substring(0, (contentX + contentWidth - cx));
                            if (t.Length > 0)
                            {
                                b.DrawText(new DL.TextRun(cx, y + renderedLines, t, color, bg, attr));
                                cx += t.Length;
                            }
                        }

                        startOffset += segmentLength;
                        renderedLines++;
                    }
                }
            }
            b.Pop();
        }

        private static IEnumerable<(string Text, DL.Rgb24 Color, DL.CellAttrFlags Attr)> Highlight(string line, string? lang)
        {
            var normal = new DL.Rgb24(200, 200, 220);
            var keyword = new DL.Rgb24(180, 220, 180);
            var typecol = new DL.Rgb24(180, 200, 240);
            var str = new DL.Rgb24(220, 200, 160);
            var com = new DL.Rgb24(120, 140, 120);
            // Comments
            if (lang != null && lang.StartsWith("py"))
            {
                int hash = line.IndexOf('#');
                string code = hash >= 0 ? line.Substring(0, hash) : line;
                foreach (var part in TokenizePython(code)) yield return part;
                if (hash >= 0) yield return (line.Substring(hash), com, DL.CellAttrFlags.None);
                yield break;
            }
            else // default to C#-like
            {
                int sl = line.IndexOf("//");
                string code = sl >= 0 ? line.Substring(0, sl) : line;
                foreach (var part in TokenizeCSharp(code)) yield return part;
                if (sl >= 0) yield return (line.Substring(sl), com, DL.CellAttrFlags.None);
                yield break;
            }

            static IEnumerable<(string, DL.Rgb24, DL.CellAttrFlags)> TokenizeCSharp(string code)
            {
                var keywords = new HashSet<string>(new[] { "using", "namespace", "class", "public", "private", "protected", "internal", "static", "void", "int", "string", "var", "new", "return", "async", "await", "if", "else", "for", "foreach", "while", "switch", "case", "break", "true", "false" });
                int i = 0; while (i < code.Length)
                {
                    char c = code[i];
                    if (char.IsWhiteSpace(c)) { int j = i; while (j < code.Length && char.IsWhiteSpace(code[j])) j++; yield return (code.Substring(i, j - i), new DL.Rgb24(200, 200, 220), DL.CellAttrFlags.None); i = j; continue; }
                    if (c == '"') { int j = i + 1; while (j < code.Length && code[j] != '"') { if (code[j] == '\\' && j + 1 < code.Length) j += 2; else j++; } j = Math.Min(code.Length, j + 1); yield return (code.Substring(i, j - i), new DL.Rgb24(220, 200, 160), DL.CellAttrFlags.None); i = j; continue; }
                    if (char.IsLetter(c) || c == '_') { int j = i + 1; while (j < code.Length && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) j++; var tok = code.Substring(i, j - i); var col = keywords.Contains(tok) ? new DL.Rgb24(180, 220, 180) : new DL.Rgb24(200, 200, 220); yield return (tok, col, DL.CellAttrFlags.None); i = j; continue; }
                    yield return (code[i].ToString(), new DL.Rgb24(200, 200, 220), DL.CellAttrFlags.None); i++;
                }
            }
            static IEnumerable<(string, DL.Rgb24, DL.CellAttrFlags)> TokenizePython(string code)
            {
                var keywords = new HashSet<string>(new[] { "def", "class", "return", "if", "elif", "else", "for", "while", "import", "from", "as", "True", "False", "None", "in", "and", "or", "not", "with", "yield" });
                int i = 0; while (i < code.Length)
                {
                    char c = code[i];
                    if (char.IsWhiteSpace(c)) { int j = i; while (j < code.Length && char.IsWhiteSpace(code[j])) j++; yield return (code.Substring(i, j - i), new DL.Rgb24(200, 200, 220), DL.CellAttrFlags.None); i = j; continue; }
                    if (c == '"' || c == '\'') { char q = c; int j = i + 1; while (j < code.Length && code[j] != q) { if (code[j] == '\\' && j + 1 < code.Length) j += 2; else j++; } j = Math.Min(code.Length, j + 1); yield return (code.Substring(i, j - i), new DL.Rgb24(220, 200, 160), DL.CellAttrFlags.None); i = j; continue; }
                    if (char.IsLetter(c) || c == '_') { int j = i + 1; while (j < code.Length && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) j++; var tok = code.Substring(i, j - i); var col = keywords.Contains(tok) ? new DL.Rgb24(180, 220, 180) : new DL.Rgb24(200, 200, 220); yield return (tok, col, DL.CellAttrFlags.None); i = j; continue; }
                    yield return (code[i].ToString(), new DL.Rgb24(200, 200, 220), DL.CellAttrFlags.None); i++;
                }
            }
        }
    }

    /// <summary>User message bubble with rounded-ish border and colored label.</summary>
    /// <summary>Spacer item that adds empty lines.</summary>
    public sealed class SpacerItem : IFeedItem
    {
        private readonly int _lines;
        public SpacerItem(int lines = 1) { _lines = Math.Max(1, lines); }
        public int MeasureLineCount(int width) => _lines;
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            // Just render empty space - no content needed
        }
    }

    public sealed class UserBubbleItem : IFeedItem
    {
        private readonly string[] _lines;
        public UserBubbleItem(string text) { _lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); }
        public int MeasureLineCount(int width) => Math.Max(1, _lines.Length + 2); // top and bottom border rows
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int total = _lines.Length + 2;
            int end = Math.Min(total, startLine + maxLines);
            var borderColor = new DL.Rgb24(120, 180, 255); // light blue
            var labelColor = new DL.Rgb24(150, 200, 255);
            for (int i = startLine; i < end; i++)
            {
                int row = y + (i - startLine);
                if (i == 0)
                {
                    // top border with rounded corners
                    int inner = Math.Max(0, width - 2);
                    b.DrawText(new DL.TextRun(x, row, "╭" + new string('─', inner) + "╮", borderColor, new DL.Rgb24(0, 0, 0), DL.CellAttrFlags.None));
                }
                else if (i == total - 1)
                {
                    // bottom border with rounded corners
                    int inner = Math.Max(0, width - 2);
                    b.DrawText(new DL.TextRun(x, row, "╰" + new string('─', inner) + "╯", borderColor, new DL.Rgb24(0, 0, 0), DL.CellAttrFlags.None));
                }
                else
                {
                    // content line with side borders
                    string content = _lines[i - 1];
                    if (i == 1)
                    {
                        // show label on first content row
                        string label = "You:";
                        b.DrawText(new DL.TextRun(x + 2, row, label + " ", labelColor, new DL.Rgb24(0, 0, 0), DL.CellAttrFlags.Bold));
                        int available = Math.Max(0, width - 4 - (label.Length + 1));
                        string t = available > 0 ? (content.Length > available ? content.Substring(0, available) : content) : string.Empty;
                        b.DrawText(new DL.TextRun(x + 2 + label.Length + 1, row, t, new DL.Rgb24(220, 220, 220), new DL.Rgb24(0, 0, 0), DL.CellAttrFlags.None));
                    }
                    else
                    {
                        int available = Math.Max(0, width - 4);
                        string t = content.Length > available ? content.Substring(0, available) : content;
                        b.DrawText(new DL.TextRun(x + 2, row, t, new DL.Rgb24(220, 220, 220), new DL.Rgb24(0, 0, 0), DL.CellAttrFlags.None));
                    }
                    if (width >= 1) b.DrawText(new DL.TextRun(x, row, "│", borderColor, new DL.Rgb24(0, 0, 0), DL.CellAttrFlags.None));
                    if (width >= 2) b.DrawText(new DL.TextRun(x + width - 1, row, "│", borderColor, new DL.Rgb24(0, 0, 0), DL.CellAttrFlags.None));
                }
            }
        }
    }

    /// <summary>Tool execution display with dotted yellow line on the left side.</summary>
    public sealed class ToolExecutionItem : IFeedItem
    {
        private readonly string _toolId;
        private readonly Dictionary<string, object?> _parameters = new();
        private readonly string? _result;
        private readonly bool _isSuccess;
        private readonly string _headerLine;
        private readonly string _paramLine = "";
        private readonly string _resultLine = "";

        public ToolExecutionItem(string toolId, Dictionary<string, object?> parameters, string? result = null, bool isSuccess = true)
        {
            _toolId = toolId;
            _parameters = parameters;
            _result = result;
            _isSuccess = isSuccess;
            // Compact header (status + tool id)
            var statusIcon = _isSuccess ? "✔" : "✖";
            _headerLine = $"{statusIcon} {toolId}";

            // Inline parameter summary (first 3 key=value)
            if (_parameters?.Any() == true)
            {
                var pairs = _parameters.Take(3)
                    .Select(kv => $"{kv.Key}={TruncateInline(kv.Value)}");
                var more = _parameters.Count > 3 ? $", +{_parameters.Count - 3} more" : "";
                _paramLine = string.Join(", ", pairs) + more;
            }

            // One-line result summary
            if (!string.IsNullOrWhiteSpace(_result))
            {
                if (_toolId == "list_directory" && _result!.Contains("\"items\""))
                {
                    var (count, dirs) = TrySummarizeDirectoryItems(_result!);
                    if (count >= 0)
                    {
                        _resultLine = $"{count} entries" + (dirs >= 0 ? $", {dirs} directories" : "");
                    }
                    else
                    {
                        _resultLine = FirstLine(_result!);
                    }
                }
                else
                {
                    _resultLine = FirstLine(_result!);
                }
            }
        }

        private static string TruncateInline(object? value)
        {
            var s = value?.ToString() ?? "null";
            if (s.Length > 40) s = s.Substring(0, 37) + "...";
            // Collapse whitespace
            return s.Replace("\n", " ").Replace("\r", " ").Trim();
        }

        private static string FirstLine(string s)
        {
            var line = s.Replace("\r\n", "\n").Replace('\r', '\n');
            var nl = line.IndexOf('\n');
            if (nl >= 0) line = line.Substring(0, nl);
            if (line.Length > 100) line = line.Substring(0, 97) + "...";
            return line.Trim();
        }

        private static (int count, int dirs) TrySummarizeDirectoryItems(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return (-1, -1);
                int total = 0, dirs = 0;
                foreach (var it in items.EnumerateArray())
                {
                    total++;
                    if (it.TryGetProperty("type", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String && t.GetString() == "directory")
                        dirs++;
                }
                return (total, dirs);
            }
            catch { return (-1, -1); }
        }

        public int MeasureLineCount(int width)
        {
            // Compact: up to 3 lines (header, params, result)
            int lines = 1;
            if (!string.IsNullOrEmpty(_paramLine)) lines++;
            if (!string.IsNullOrEmpty(_resultLine)) lines++;
            return lines;
        }

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (width <= 0 || maxLines <= 0) return;

            int row = y;
            int drawn = 0;
            var green = new DL.Rgb24(60, 200, 120);
            var red = new DL.Rgb24(240, 100, 100);
            var cyan = new DL.Rgb24(120, 200, 255);
            var dim = new DL.Rgb24(170, 170, 170);
            var fg = _isSuccess ? green : red;

            // Header (tool name highlighted)
            if (drawn < maxLines && startLine <= 0)
            {
                var t = _headerLine;
                if (t.Length > width) t = t.Substring(0, Math.Max(0, width - 1));
                b.DrawText(new DL.TextRun(x, row, t, cyan, null, DL.CellAttrFlags.Bold));
                row++; drawn++;
            }

            // Inline parameters
            if (!string.IsNullOrEmpty(_paramLine) && drawn < maxLines && startLine <= 1)
            {
                var t = _paramLine;
                if (t.Length > width) t = t.Substring(0, Math.Max(0, width - 1)) + "…";
                b.DrawText(new DL.TextRun(x, row, t, dim, null, DL.CellAttrFlags.None));
                row++; drawn++;
            }

            // One-line result
            if (!string.IsNullOrEmpty(_resultLine) && drawn < maxLines)
            {
                var label = "Result: ";
                var space = Math.Max(0, width - label.Length);
                var body = _resultLine.Length > space ? _resultLine.Substring(0, Math.Max(0, space - 1)) + "…" : _resultLine;
                b.DrawText(new DL.TextRun(x, row, label, dim, null, DL.CellAttrFlags.None));
                b.DrawText(new DL.TextRun(x + label.Length, row, body, new DL.Rgb24(210, 210, 210), null, DL.CellAttrFlags.None));
            }
        }
    }

    /// <summary>A streaming message that can be updated progressively.</summary>
    public sealed class StreamingMessageItem : IFeedItem
    {
        private readonly System.Text.StringBuilder _content = new();
        private bool _completed = false;
        private bool _hidden = false;
        private string[] _lines = Array.Empty<string>();

        /// <summary>Append content to the streaming message.</summary>
        public void AppendContent(string content)
        {
            if (!_completed)
            {
                _content.Append(content);
                UpdateLines();
            }
        }

        /// <summary>Mark the streaming message as complete.</summary>
        public void Complete()
        {
            _completed = true;
        }

        /// <summary>Hide the streaming message (used when content will be reformatted).</summary>
        public void Hide()
        {
            _hidden = true;
            _lines = Array.Empty<string>();
        }

        /// <summary>Get the full content of the streaming message.</summary>
        public string GetContent()
        {
            return _content.ToString();
        }

        private void UpdateLines()
        {
            if (!_hidden)
            {
                _lines = _content.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            }
        }

        /// <inheritdoc />
        public int MeasureLineCount(int width)
        {
            if (_content.Length == 0) return 1;

            // Count wrapped lines
            int totalLines = 0;
            foreach (var line in _lines)
            {
                if (string.IsNullOrEmpty(line))
                    totalLines++;
                else
                    totalLines += Math.Max(1, (int)Math.Ceiling((double)line.Length / Math.Max(1, width)));
            }
            return Math.Max(1, totalLines);
        }

        /// <inheritdoc />
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (_content.Length == 0)
            {
                // Show a loading indicator if still streaming and no content yet
                if (!_completed)
                {
                    b.DrawText(new DL.TextRun(x, y, "...", new DL.Rgb24(150, 150, 150), null, DL.CellAttrFlags.None));
                }
                return;
            }

            // Render as simple markdown-styled text
            var renderer = new Andy.Tui.Widgets.MarkdownRenderer();
            renderer.SetText(_content.ToString());
            renderer.Render(new L.Rect(x, y, width, maxLines), baseDl, b);

            // Show a blinking cursor at the end if still streaming
            if (!_completed && _lines.Length > 0)
            {
                var lastLine = _lines[_lines.Length - 1];
                int cursorX = x + (lastLine.Length % width);
                int cursorY = y + Math.Min(maxLines - 1, MeasureLineCount(width) - 1 - startLine);

                if (cursorY >= y && cursorY < y + maxLines)
                {
                    // Simple blinking cursor effect
                    var cursor = (DateTime.Now.Millisecond / 500) % 2 == 0 ? "▊" : " ";
                    b.DrawText(new DL.TextRun(cursorX, cursorY, cursor,
                        new DL.Rgb24(100, 200, 100), null, DL.CellAttrFlags.Bold));
                }
            }
        }
    }
}
