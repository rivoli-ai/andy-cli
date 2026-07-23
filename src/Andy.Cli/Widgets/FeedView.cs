using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Process-wide VIEW state for how much tool-execution detail the feed renders.
    ///
    /// This is a pure presentation toggle: it only changes how already-recorded tool
    /// items (<see cref="ToolExecutionItem"/> / <see cref="RunningToolItem"/>) lay out
    /// and draw on the next frame. It does NOT touch the assistant turn, the tool
    /// executor, or any recorded data — flipping it while a turn is in flight is safe
    /// and only reflows the display. The render loop runs continuously (~60fps) on the
    /// UI thread and the assistant turn runs on a background Task, so toggling this flag
    /// re-measures and re-renders existing items without affecting the running turn.
    ///
    /// Ctrl+O in Program.cs flips <see cref="Expanded"/>. Because feed items read this
    /// flag at measure/render time (not at construction), the change applies retroactively
    /// to every tool item on screen, both completed and in-flight.
    /// </summary>
    public static class ToolOutputView
    {
        // volatile: written on the UI/input thread, read on the render path (same thread
        // today, but marked volatile to make the cross-thread-safe intent explicit and
        // robust if rendering is ever moved off the input thread).
        private static volatile bool _expanded;

        /// <summary>True when tool items should render full parameters and a multi-line
        /// result preview; false for the compact one/two-line summary.</summary>
        public static bool Expanded
        {
            get => _expanded;
            set => _expanded = value;
        }

        /// <summary>Flip expanded/collapsed and return the new state. Pure view toggle.</summary>
        public static bool Toggle() => _expanded = !_expanded;
    }

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

        // Auto-scroll (bottom-follow) state.
        // The feed auto-scrolls to the bottom when new content arrives, but only
        // while the user is "pinned to bottom" (at or near the bottom) or has been
        // idle for a while after scrolling. When the user scrolls up to read
        // history, auto-scroll pauses and appended content does not yank the view.

        /// <summary>How close (in lines from the bottom) still counts as "pinned to bottom".
        /// Scrolling within this band keeps auto-scroll enabled.</summary>
        public const int PinnedToBottomThresholdLines = 2;

        /// <summary>How long the user must be idle after scrolling before auto-scroll resumes.</summary>
        public static readonly TimeSpan AutoScrollIdleResumeThreshold = TimeSpan.FromSeconds(10);

        // Injectable clock so idle-timer behavior is testable without the wall clock.
        private Func<DateTime> _clock = () => DateTime.UtcNow;
        private DateTime _lastScrollActivityUtc = DateTime.MinValue;

        /// <summary>True when auto-scroll is currently enabled (the view follows new content to the bottom).
        /// This is the case while the user is pinned at/near the bottom.</summary>
        public bool IsAutoScrollEnabled => _scrollOffset <= PinnedToBottomThresholdLines;

        // Number of items appended while the user was scrolled up reading history.
        // Rendered as a "N new messages" overlay on the last visible line so the
        // user knows there is pending content below; cleared as soon as the view
        // returns to (or near) the bottom.
        private int _newItemsBelow;

        /// <summary>Items appended below the viewport while the user was scrolled up.
        /// Zero whenever the view is at/near the bottom.</summary>
        public int NewItemsBelowCount => _newItemsBelow;

        /// <summary>True when the "N new messages" overlay should be drawn: the user is
        /// scrolled up reading history and content has been appended below.</summary>
        public bool IsNewItemsOverlayVisible => _newItemsBelow > 0 && _scrollOffset > PinnedToBottomThresholdLines;

        /// <summary>Overlay label for a given pending-item count (plain ASCII, no emojis).</summary>
        internal static string FormatNewItemsOverlay(int count)
            => count == 1 ? "1 new message" : $"{count} new messages";

        /// <summary>Replace the time source used for the idle auto-scroll timer (testing hook).</summary>
        internal void SetClockForTesting(Func<DateTime> clock) => _clock = clock ?? (() => DateTime.UtcNow);

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
                    OnContentAppended();
                }
            }
        }

        /// <summary>
        /// Decide how appended content affects the scroll position.
        /// Must be called while holding <see cref="_itemsLock"/>.
        ///  - If auto-scroll is enabled (pinned to bottom, or idle long enough after
        ///    scrolling), snap to the bottom so the new content is visible.
        ///  - Otherwise the user is reading history, so hold their view steady by
        ///    growing the offset-from-bottom as content is appended below.
        /// </summary>
        private void OnContentAppended()
        {
            bool idleResume = _scrollOffset > PinnedToBottomThresholdLines
                && _lastScrollActivityUtc != DateTime.MinValue
                && (_clock() - _lastScrollActivityUtc) >= AutoScrollIdleResumeThreshold;

            if (IsAutoScrollEnabled || idleResume)
            {
                // Resume / stay pinned to the bottom.
                _scrollOffset = 0;
                _followTail = true;
                _lastScrollActivityUtc = DateTime.MinValue;
                _newItemsBelow = 0; // the new content is (about to be) visible
            }
            else
            {
                // User is scrolled up reading; keep their anchor fixed by growing
                // the offset by however many lines the new item adds. The actual
                // line delta is reconciled against the measured total during Render
                // (see _autoScrollHoldAnchor), so here we only mark that a hold is
                // pending. Render clamps it to valid bounds.
                _autoScrollHoldAnchor = true;
                // The appended item lands below the viewport; surface it in the
                // "N new messages" overlay until the user scrolls back down.
                _newItemsBelow++;
            }
        }

        // When set, the next Render preserves the user's view across appended
        // content by increasing _scrollOffset by the number of lines added.
        private bool _autoScrollHoldAnchor;
        /// <summary>Convenience: append markdown item.</summary>
        public void AddMarkdown(string md) => AddItem(new MarkdownItem(md));
        /// <summary>Convenience: append markdown using Andy.Tui.Widgets.MarkdownRenderer to better handle inline formatting. Detects and renders markdown tables separately.</summary>
        public void AddMarkdownRich(string md)
        {
            // A model that opens a ``` code fence and forgets to close it would otherwise turn
            // everything after it into a code block - bold and inline-code markers then render
            // literally (the "**Fix scope**" + visible-backticks bug). Neutralize a dangling fence
            // first so the prose after it renders as markdown.
            md = FeedMarkdown.BalanceCodeFences(md);
            // Split markdown by tables and render each part appropriately
            var parts = SplitMarkdownWithTables(md);
            foreach (var part in parts)
            {
                if (part.IsTable)
                {
                    AddItem(new TableItem(part.Headers!, part.Rows!, part.Title));
                }
                else if (!string.IsNullOrWhiteSpace(part.Content))
                {
                    AddItem(new MarkdownRendererItem(part.Content));
                }
            }
        }
        /// <summary>Convenience: append code block item.</summary>
        public void AddCode(string code, string? language = null) => AddItem(new CodeBlockItem(code, language));
        /// <summary>Append a git-style diff for a file write/update operation.</summary>
        public void AddFileDiff(string displayPath, FileChangeKind kind, FileDiff diff) => AddItem(new FileDiffItem(displayPath, kind, diff));
        /// <summary>Append a user message bubble with a rounded frame and label.</summary>
        public void AddUserMessage(string text, int messageNumber = 0)
        {
            // Add spacing before user messages for better readability
            AddItem(new SpacerItem(1));
            AddItem(new UserBubbleItem(text, messageNumber));
            // Add spacing after user messages to separate from response
            AddItem(new SpacerItem(1));
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

        /// <summary>Add a tool execution start with animation.</summary>
        public void AddToolExecutionStart(string toolId, string toolName, Dictionary<string, object?>? parameters = null)
        {
            var item = new RunningToolItem(toolId, toolName);
            if (parameters != null)
            {
                item.SetParameters(parameters);
            }
            AddItem(item);
            // Blank line after every tool so consecutive tools are visually separated.
            AddItem(new SpacerItem(1));
        }

        /// <summary>Add detail to a running tool execution.</summary>
        public void AddToolExecutionDetail(string toolId, string detail)
        {
            // Find and update the running tool item
            lock (_itemsLock)
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] is RunningToolItem runningTool && runningTool.ToolId == toolId && !runningTool.IsComplete)
                    {
                        runningTool.AddDetail(detail);
                        break;
                    }
                }
            }
        }

        /// <summary>Update running tool with actual parameters from ToolAdapter.</summary>
        public void UpdateRunningToolParameters(string toolName, Dictionary<string, object?> parameters)
        {
            lock (_itemsLock)
            {
                // Find the most recent running tool with matching name (be flexible with matching)
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] is RunningToolItem runningTool &&
                        !runningTool.IsComplete)
                    {
                        // More flexible matching - handle different naming conventions
                        var normalizedToolName = toolName.ToLower().Replace("_", "").Replace("-", "").Replace(" ", "");
                        var normalizedRunningName = runningTool.ToolName.ToLower().Replace("_", "").Replace("-", "").Replace(" ", "");

                        if (!normalizedRunningName.Equals(normalizedToolName, StringComparison.OrdinalIgnoreCase) &&
                            !normalizedRunningName.Contains(normalizedToolName) &&
                            !normalizedToolName.Contains(normalizedRunningName))
                        {
                            continue; // Not a match
                        }
                        runningTool.SetParameters(parameters);

                        // Add details about what's being searched/indexed
                        if (toolName.Contains("code_index", StringComparison.OrdinalIgnoreCase))
                        {
                            if (parameters.TryGetValue("query", out var query) && query != null)
                            {
                                runningTool.AddDetail($"Query: {query}");
                            }
                            if (parameters.TryGetValue("namespace", out var ns) && ns != null)
                            {
                                runningTool.AddDetail($"Namespace: {ns}");
                            }
                            if (parameters.TryGetValue("filter", out var filter) && filter != null)
                            {
                                runningTool.AddDetail($"Filter: {filter}");
                            }
                        }
                        else if (toolName.Contains("list_directory", StringComparison.OrdinalIgnoreCase))
                        {
                            // Try both parameter names for directory
                            object? dirPath = null;
                            if (!parameters.TryGetValue("directory_path", out dirPath))
                                parameters.TryGetValue("path", out dirPath);

                            if (dirPath != null)
                            {
                                runningTool.AddDetail($"Directory: {dirPath}");
                            }

                            // Add other significant parameters
                            if (parameters.TryGetValue("recursive", out var rec) && rec?.ToString() == "True")
                            {
                                runningTool.AddDetail("Mode: Recursive");
                            }
                            if (parameters.TryGetValue("pattern", out var pattern) && pattern != null)
                            {
                                runningTool.AddDetail($"Pattern: {pattern}");
                            }
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>Update a tool by its exact ID - this is the most direct way to update a tool.</summary>
        public void UpdateToolByExactId(string exactToolId, Dictionary<string, object?> parameters)
        {
            lock (_itemsLock)
            {
                // Look for any running tool item, starting from the most recent
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] is RunningToolItem runningTool && !runningTool.IsComplete)
                    {
                        // Check if this tool has the exact ID we're looking for
                        // The tool ID should be in its parameters as __toolId
                        if (runningTool.Parameters != null &&
                            runningTool.Parameters.TryGetValue("__toolId", out var storedId) &&
                            storedId?.ToString() == exactToolId)
                        {
                            // Found the exact match! Update with real parameters
                            // Preserve the __toolId and __baseName for identification
                            var mergedParams = new Dictionary<string, object?>(parameters);
                            if (runningTool.Parameters.TryGetValue("__toolId", out var tid))
                                mergedParams["__toolId"] = tid;
                            if (runningTool.Parameters.TryGetValue("__baseName", out var bn))
                                mergedParams["__baseName"] = bn;

                            runningTool.SetParameters(mergedParams);
                            return; // Found and updated the exact tool
                        }
                    }
                }

                // If we couldn't find by exact ID, try to update the most recent incomplete tool
                // This handles cases where the tool ID doesn't match exactly
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] is RunningToolItem runningTool && !runningTool.IsComplete)
                    {
                        // If this tool has minimal parameters (just __toolId and __baseName), update it
                        if (runningTool.Parameters == null || runningTool.Parameters.Count <= 2)
                        {
                            var mergedParams = new Dictionary<string, object?>(parameters);
                            if (runningTool.Parameters != null)
                            {
                                if (runningTool.Parameters.TryGetValue("__toolId", out var tid))
                                    mergedParams["__toolId"] = tid;
                                if (runningTool.Parameters.TryGetValue("__baseName", out var bn))
                                    mergedParams["__baseName"] = bn;
                            }
                            runningTool.SetParameters(mergedParams);
                            return; // Updated the most recent tool needing parameters
                        }
                    }
                }
            }
        }

        /// <summary>FORCE update ALL matching tools with parameters - be aggressive about finding matches.</summary>
        public void ForceUpdateAllMatchingTools(string toolId, string toolName, Dictionary<string, object?> parameters)
        {
            lock (_itemsLock)
            {
                // Update EVERY running tool that could possibly match
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] is RunningToolItem runningTool && !runningTool.IsComplete)
                    {
                        var normalizedToolId = toolId.ToLower().Replace("_", "").Replace("-", "");
                        var normalizedToolName = toolName.ToLower().Replace("_", "").Replace("-", "");
                        var normalizedRunningName = runningTool.ToolName.ToLower().Replace("_", "").Replace("-", "").Replace(" ", "");

                        // Match if ANY of these conditions are true
                        if (normalizedRunningName.Contains(normalizedToolId) ||
                            normalizedRunningName.Contains(normalizedToolName) ||
                            normalizedToolId.Contains(normalizedRunningName) ||
                            normalizedToolName.Contains(normalizedRunningName))
                        {
                            runningTool.SetParameters(parameters);

                            // Log that we updated it
                            if (parameters.TryGetValue("file_path", out var fp))
                            {
                                runningTool.AddDetail($"File: {fp}");
                            }
                            else if (parameters.TryGetValue("directory_path", out var dp))
                            {
                                runningTool.AddDetail($"Directory: {dp}");
                            }
                            else if (parameters.TryGetValue("query", out var q))
                            {
                                runningTool.AddDetail($"Query: {q}");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Update any active tool that matches the base tool ID with parameters.</summary>
        public void UpdateActiveToolWithParameters(string baseToolId, Dictionary<string, object?> parameters)
        {
            lock (_itemsLock)
            {
                // Find any running tool that matches the base tool ID
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] is RunningToolItem runningTool && !runningTool.IsComplete)
                    {
                        var toolNameNormalized = runningTool.ToolName.ToLower().Replace(" ", "_").Replace("-", "_");

                        // Check if this running tool matches our base tool ID
                        if (toolNameNormalized.Contains(baseToolId.ToLower()) ||
                            baseToolId.ToLower().Contains(toolNameNormalized))
                        {
                            runningTool.SetParameters(parameters);

                            // Add specific details based on tool type and parameters
                            if (baseToolId.Contains("read_file"))
                            {
                                if (parameters.TryGetValue("file_path", out var filePath) && filePath != null)
                                {
                                    var fileName = Path.GetFileName(filePath.ToString() ?? "");
                                    runningTool.AddDetail($"Reading: {fileName}");
                                }
                            }
                            else if (baseToolId.Contains("list_directory"))
                            {
                                // Try both parameter names
                                object? dirPath = null;
                                if (!parameters.TryGetValue("directory_path", out dirPath))
                                    parameters.TryGetValue("path", out dirPath);

                                if (dirPath != null)
                                {
                                    var pathStr = dirPath.ToString() ?? ".";
                                    runningTool.AddDetail($"Listing: {pathStr}");
                                }
                            }
                            else if (baseToolId.Contains("write_file"))
                            {
                                if (parameters.TryGetValue("file_path", out var filePath) && filePath != null)
                                {
                                    var fileName = Path.GetFileName(filePath.ToString() ?? "");
                                    runningTool.AddDetail($"Writing: {fileName}");
                                }
                            }

                            break; // Update only the first matching tool
                        }
                    }
                }
            }
        }

        /// <summary>Update tool execution to complete state.</summary>
        public void AddToolExecutionComplete(string toolId, bool success, string duration, string? result = null)
        {
            // Find and update the running tool item
            lock (_itemsLock)
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] is RunningToolItem runningTool && runningTool.ToolId == toolId)
                    {
                        // Idempotent: the tool executor marks completion the instant the tool
                        // returns (stopping the spinner with the tool's real duration). A later
                        // end-of-turn pass must not overwrite that with the whole-turn elapsed.
                        if (runningTool.IsComplete)
                        {
                            break;
                        }
                        runningTool.SetComplete(success, duration);
                        if (!string.IsNullOrEmpty(result))
                        {
                            runningTool.SetResult(result);
                        }
                        // Don't add spacing here - spacing is managed by the caller
                        break;
                    }
                }
            }
        }

        /// <summary>Add processing indicator with animation.</summary>
        /// <param name="stats">Optional live turn metrics rendered alongside the spinner.</param>
        public void AddProcessingIndicator(TurnStats? stats = null)
        {
            AddItem(new ProcessingIndicatorItem(stats));
        }

        /// <summary>Clear processing indicator.</summary>
        public void ClearProcessingIndicator()
        {
            lock (_itemsLock)
            {
                _items.RemoveAll(item => item is ProcessingIndicatorItem);
            }
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
            _autoScrollHoldAnchor = false;
            _lastScrollActivityUtc = DateTime.MinValue;
            _newItemsBelow = 0;
        }

        /// <summary>Scroll the feed by delta lines (positive = up). Returns current offset.</summary>
        public int ScrollLines(int delta, int pageSize)
        {
            int total = _totalLinesCache;
            if (total <= 0) return _scrollOffset;
            if (delta == int.MaxValue) delta = pageSize;
            if (delta == int.MinValue) delta = -pageSize;

            // Calculate new scroll offset with bounds
            int newOffset = _scrollOffset + delta;

            // Clamp to valid range: 0 (bottom/following tail) up to the highest
            // offset that still shows content, i.e. total minus the viewport
            // height. Clamping to `total` instead would create a dead-zone above
            // the top of the content where scrolling back down does nothing.
            int maxOffset = Math.Max(0, total - _lastViewportHeight);
            _scrollOffset = Math.Clamp(newOffset, 0, maxOffset);
            _followTail = _scrollOffset == 0;

            // Record the moment of this scroll so the idle timer can later decide
            // whether auto-scroll should resume. A pending hold is no longer needed
            // because the user explicitly moved the view.
            _autoScrollHoldAnchor = false;
            if (_scrollOffset <= PinnedToBottomThresholdLines)
            {
                // Back at/near the bottom: auto-scroll is active again and the
                // pending content is visible, so the overlay disappears.
                _lastScrollActivityUtc = DateTime.MinValue;
                _newItemsBelow = 0;
            }
            else
            {
                _lastScrollActivityUtc = _clock();
            }
            return _scrollOffset;
        }

        /// <summary>
        /// Snap the view to the bottom and re-enable auto-scroll (pin to bottom).
        /// Used when the user starts typing into the prompt: the feed should return
        /// to where the prompt is, regardless of how far up they had scrolled.
        /// This resets <see cref="_scrollOffset"/> to 0 and clears any pending hold
        /// or idle-timer state so the feed follows new content again.
        /// </summary>
        public void SnapToBottom()
        {
            _scrollOffset = 0;
            _followTail = true;
            _autoScrollHoldAnchor = false;
            _lastScrollActivityUtc = DateTime.MinValue;
            _newItemsBelow = 0;
        }

        /// <summary>Advance animation state one frame.</summary>
        public void Tick()
        {
            if (_animRemaining > 0) _animRemaining = Math.Max(0, _animRemaining - _animSpeed);
        }

        /// <summary>Number of feed items. Changes when content is added/removed/cleared.</summary>
        public int ItemCount { get { lock (_itemsLock) { return _items.Count; } } }

        /// <summary>Total wrapped line count from the last <see cref="Render"/> (content reflow signal).</summary>
        public int RenderedLineCount => _totalLinesCache;

        /// <summary>Current scroll offset in lines from the bottom (0 = following the tail).</summary>
        public int ScrollOffset => _scrollOffset;

        private int _totalLinesCache;
        private int _lastViewportHeight;

        /// <summary>Render feed items inside rect, stacking vertically with bottom alignment when following tail.</summary>
        public void Render(in L.Rect rect, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;

            // Guard against invalid dimensions to prevent crashes
            if (w <= 0 || h <= 0) return;

            b.PushClip(new DL.ClipPush(x, y, w, h));
            // Paint the whole viewport with the theme background every frame so cells that the
            // items no longer cover are cleared. Without this, when feed content shrinks or reflows
            // (e.g. a tool item changing height, or the variable-height file-diff items), glyphs and
            // colors from a taller previous frame linger - phantom characters and whitespace that
            // does not match the theme. A null (transparent) theme background clears to the terminal
            // default, so terminal transparency is preserved.
            b.DrawRect(new DL.Rect(x, y, w, h, Themes.Theme.Current.Background));
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

            // If the user is reading history and content was appended below the
            // viewport, hold their view steady by growing the offset-from-bottom by
            // the number of lines added. This keeps the same content under their
            // eyes instead of letting it scroll away.
            if (_autoScrollHoldAnchor && total > _prevTotalLines)
            {
                int added = total - _prevTotalLines;
                int maxOffset = Math.Max(0, total - h);
                _scrollOffset = Math.Clamp(_scrollOffset + added, 0, maxOffset);
                _followTail = _scrollOffset == 0;
            }
            _autoScrollHoldAnchor = false;

            // Update animation on growth
            if (_followTail && _scrollOffset == 0 && total > _prevTotalLines)
            {
                _animRemaining = Math.Min(_animRemaining + (total - _prevTotalLines), total);
            }
            _prevTotalLines = total; _totalLinesCache = total;
            _lastViewportHeight = h; // remember for scroll-offset clamping

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

            // When the user is scrolled up and content has been appended below,
            // overlay a pending-content notice on the last visible line so they
            // know there is new output waiting at the bottom. Plain ASCII.
            if (IsNewItemsOverlayVisible)
            {
                string label = " " + FormatNewItemsOverlay(_newItemsBelow) + " ";
                if (label.Length <= w)
                {
                    int lx = x + Math.Max(0, w - label.Length - 1);
                    int ly = y + h - 1;
                    b.DrawText(new DL.TextRun(lx, ly, label,
                        new DL.Rgb24(100, 150, 255), new DL.Rgb24(30, 30, 40), DL.CellAttrFlags.Bold));
                }
            }

            b.Pop();
        }

        /// <summary>
        /// Update tool result with detailed information from the actual tool execution
        /// </summary>
        public void UpdateToolResult(string toolId, string toolName, bool success, object? resultData, Dictionary<string, object?>? parameters)
        {
            // Extract meaningful result summary based on tool type and actual result data
            string? resultSummary = null;

            if (toolName.Contains("read_file", StringComparison.OrdinalIgnoreCase))
            {
                // For read_file, show what file was read and its characteristics
                if (parameters?.TryGetValue("file_path", out var filePath) == true && filePath != null)
                {
                    var fileName = Path.GetFileName(filePath.ToString() ?? "");

                    // Try to extract metadata from result
                    if (resultData is Dictionary<string, object?> resultDict)
                    {
                        if (resultDict.TryGetValue("metadata", out var metadata) && metadata is Dictionary<string, object?> metaDict)
                        {
                            var parts = new List<string> { $"Read {fileName}" };

                            if (metaDict.TryGetValue("line_count", out var lines))
                                parts.Add($"{lines} lines");
                            if (metaDict.TryGetValue("file_size_formatted", out var size))
                                parts.Add($"{size}");
                            if (metaDict.TryGetValue("encoding", out var encoding))
                                parts.Add($"{encoding}");

                            resultSummary = string.Join(", ", parts);
                        }
                        else if (resultDict.TryGetValue("content", out var content) && content is string contentStr)
                        {
                            var lineCount = contentStr.Split('\n').Length;
                            resultSummary = $"Read {fileName} ({lineCount} lines, {contentStr.Length} chars)";
                        }
                    }

                    if (string.IsNullOrEmpty(resultSummary))
                        resultSummary = $"Read {fileName}";
                }
            }
            else if (toolName.Contains("write_file", StringComparison.OrdinalIgnoreCase))
            {
                if (parameters?.TryGetValue("file_path", out var filePath) == true && filePath != null)
                {
                    var fileName = Path.GetFileName(filePath.ToString() ?? "");
                    if (parameters.TryGetValue("content", out var content) && content != null)
                    {
                        var contentStr = content.ToString() ?? "";
                        var lineCount = contentStr.Split('\n').Length;
                        resultSummary = $"Wrote {fileName} ({lineCount} lines, {contentStr.Length} chars)";
                    }
                    else
                    {
                        resultSummary = $"Wrote {fileName}";
                    }
                }
            }
            else if (toolName.Contains("delete_file", StringComparison.OrdinalIgnoreCase))
            {
                if (parameters?.TryGetValue("file_path", out var filePath) == true && filePath != null)
                {
                    var fileName = Path.GetFileName(filePath.ToString() ?? "");
                    resultSummary = $"Deleted {fileName}";
                }
            }
            else if (toolName.Contains("list_directory", StringComparison.OrdinalIgnoreCase))
            {
                // The tool returns "items" (FileSystemEntry objects) plus "count"/"total_count".
                // Build the listing straight from the result so it works even when the parameters
                // never reached the UI (a separate tracking miss that used to leave this "done").
                if (resultData is Dictionary<string, object?> resultDict)
                {
                    var dirs = new List<string>();
                    var files = new List<string>();
                    if (resultDict.TryGetValue("items", out var itemsObj)
                        && itemsObj is System.Collections.IEnumerable itemList && itemsObj is not string)
                    {
                        foreach (var entry in itemList)
                        {
                            if (entry == null) continue;
                            var et = entry.GetType();
                            var name = (et.GetProperty("Name")?.GetValue(entry)
                                        ?? et.GetProperty("RelativePath")?.GetValue(entry)
                                        ?? et.GetProperty("FullPath")?.GetValue(entry))?.ToString();
                            if (string.IsNullOrEmpty(name)) continue;
                            name = Path.GetFileName(name.TrimEnd('/')) is { Length: > 0 } shortName ? shortName : name;
                            bool isDir = et.GetProperty("IsDirectory")?.GetValue(entry) is bool b && b;
                            if (isDir) dirs.Add(name + "/"); else files.Add(name);
                        }
                    }

                    int total = resultDict.TryGetValue("count", out var c) && c is int ci ? ci : dirs.Count + files.Count;

                    string dirLabel = "directory";
                    if (parameters?.TryGetValue("path", out var path) == true && path != null)
                    {
                        var dn = Path.GetFileName(path.ToString()!.TrimEnd('/'));
                        dirLabel = string.IsNullOrEmpty(dn) ? path.ToString()! : dn;
                    }

                    var header = $"Listed {dirLabel}: {total} item{(total == 1 ? "" : "s")} " +
                                 $"({dirs.Count} dir{(dirs.Count == 1 ? "" : "s")}, {files.Count} file{(files.Count == 1 ? "" : "s")})";

                    var names = dirs.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                    .Concat(files.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                                    .ToList();
                    resultSummary = names.Count > 0
                        ? header + "\n" + string.Join("\n", names.Select(n => "  " + n))
                        : header;
                }
            }
            else if (toolName.Contains("bash_command", StringComparison.OrdinalIgnoreCase) ||
                     toolName.Contains("execute_command", StringComparison.OrdinalIgnoreCase))
            {
                if (parameters?.TryGetValue("command", out var command) == true && command != null)
                {
                    var cmdStr = command.ToString() ?? "";
                    // Extract just the command name (first word)
                    var cmdName = cmdStr.Split(' ').FirstOrDefault() ?? cmdStr;
                    if (cmdName.Length > 20) cmdName = cmdName.Substring(0, 17) + "...";

                    if (resultData is Dictionary<string, object?> resultDict)
                    {
                        // Prefer the actual command output so the feed shows what ran (collapsed:
                        // first line; expanded: full). Fall back to an exit-code status only when
                        // there is no output to show.
                        var output = (resultDict.TryGetValue("output", out var o) ? o?.ToString() : null)
                                  ?? (resultDict.TryGetValue("stdout", out var so) ? so?.ToString() : null);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            resultSummary = output!.TrimEnd();
                        }
                        else if (resultDict.TryGetValue("exit_code", out var exitCode))
                        {
                            resultSummary = exitCode?.ToString() == "0"
                                ? $"Executed {cmdName} successfully"
                                : $"Executed {cmdName} (exit code: {exitCode})";
                        }
                    }

                    if (string.IsNullOrEmpty(resultSummary))
                        resultSummary = $"Executed {cmdName}";
                }
            }
            else if (toolName.Contains("search", StringComparison.OrdinalIgnoreCase))
            {
                if (parameters?.TryGetValue("query", out var query) == true && query != null)
                {
                    if (resultData is Dictionary<string, object?> resultDict &&
                        resultDict.TryGetValue("matches", out var matches))
                    {
                        if (matches is IEnumerable<object> matchList)
                        {
                            var count = matchList.Count();
                            resultSummary = $"Found {count} matches for \"{query}\"";
                        }
                        else if (matches is int matchCount)
                        {
                            resultSummary = $"Found {matchCount} matches for \"{query}\"";
                        }
                    }
                    else
                    {
                        resultSummary = $"Searched for \"{query}\"";
                    }
                }
            }

            // If we have a detailed result, find and update the corresponding running tool
            if (!string.IsNullOrEmpty(resultSummary))
            {
                lock (_itemsLock)
                {
                    // Find tools that match the toolName pattern (might have counter suffix)
                    for (int i = _items.Count - 1; i >= 0; i--)
                    {
                        if (_items[i] is RunningToolItem runningTool &&
                            !runningTool.IsComplete)
                        {
                            // Match by tool name (ignoring counter suffix if present)
                            var baseToolName = runningTool.ToolName.ToLower().Replace(" ", "_").Replace("-", "_");
                            if (toolId.StartsWith(baseToolName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Update with detailed result
                                runningTool.SetResult(resultSummary);
                                break;
                            }
                        }
                    }
                }
            }
        }

        private static string CleanCellContent(string cell)
        {
            // Remove HTML br tags and convert to space
            cell = System.Text.RegularExpressions.Regex.Replace(cell, @"<br\s*/?>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Remove markdown bold markers
            cell = cell.Replace("**", "");
            // Collapse multiple spaces
            cell = System.Text.RegularExpressions.Regex.Replace(cell, @"\s+", " ");
            return cell.Trim();
        }

        /// <summary>
        /// Split one markdown table row into its cells, preserving EMPTY cells. Only the optional
        /// leading/trailing pipe is stripped; interior empty cells (e.g. "| a |  | c |") are kept so
        /// columns stay aligned. Using Split with RemoveEmptyEntries instead dropped empty cells and
        /// shifted every following column left.
        /// </summary>
        internal static List<string> SplitTableCells(string line)
        {
            var s = line.Trim();
            if (s.StartsWith("|")) s = s.Substring(1);
            if (s.EndsWith("|")) s = s.Substring(0, s.Length - 1);
            return s.Split('|').Select(CleanCellContent).ToList();
        }

        private static List<MarkdownPart> SplitMarkdownWithTables(string md)
        {
            var parts = new List<MarkdownPart>();
            var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;

            while (i < lines.Length)
            {
                // Look for table start (line with pipes)
                if (lines[i].Contains('|') && i + 1 < lines.Length && lines[i + 1].Contains('|') && lines[i + 1].Contains('-'))
                {
                    // Found potential table - parse it
                    var headerLine = lines[i];
                    var separatorLine = lines[i + 1];

                    // Extract headers (empty cells preserved so columns stay aligned)
                    var headers = SplitTableCells(headerLine);

                    if (headers.Count > 0)
                    {
                        // Extract rows
                        var rows = new List<string[]>();
                        i += 2; // Skip header and separator

                        while (i < lines.Length && lines[i].Contains('|'))
                        {
                            var cells = SplitTableCells(lines[i]).ToArray();

                            if (cells.Length > 0)
                            {
                                // Pad cells to match header count
                                if (cells.Length < headers.Count)
                                {
                                    var paddedCells = new string[headers.Count];
                                    Array.Copy(cells, paddedCells, cells.Length);
                                    for (int j = cells.Length; j < headers.Count; j++)
                                    {
                                        paddedCells[j] = "";
                                    }
                                    rows.Add(paddedCells);
                                }
                                else if (cells.Length > headers.Count)
                                {
                                    // Truncate extra cells
                                    rows.Add(cells.Take(headers.Count).ToArray());
                                }
                                else
                                {
                                    rows.Add(cells);
                                }
                            }
                            i++;
                        }

                        parts.Add(new MarkdownPart
                        {
                            IsTable = true,
                            Headers = headers,
                            Rows = rows
                        });
                        continue;
                    }
                }

                // Not a table - accumulate regular markdown
                var mdLines = new List<string>();
                while (i < lines.Length)
                {
                    // Check if next line starts a table
                    if (i + 1 < lines.Length &&
                        lines[i].Contains('|') &&
                        lines[i + 1].Contains('|') &&
                        lines[i + 1].Contains('-'))
                    {
                        break;
                    }
                    mdLines.Add(lines[i]);
                    i++;
                }

                if (mdLines.Count > 0)
                {
                    parts.Add(new MarkdownPart
                    {
                        IsTable = false,
                        Content = string.Join("\n", mdLines)
                    });
                }
            }

            return parts;
        }

        private class MarkdownPart
        {
            public bool IsTable { get; set; }
            public string? Content { get; set; }
            public List<string>? Headers { get; set; }
            public List<string[]>? Rows { get; set; }
            public string? Title { get; set; }
        }
    }


    /// <summary>Markdown feed item using a naive line-by-line renderer with fenced code detection.</summary>
    public sealed class MarkdownItem : IFeedItem
    {
        private readonly string[] _lines;
        /// <summary>Create a markdown item from raw markdown text.</summary>
        public MarkdownItem(string markdown)
        {
            // Collapse blank-line runs and ensure a blank line before headings.
            _lines = FeedMarkdown.Normalize(markdown ?? string.Empty).Split('\n');
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
            var theme = Themes.Theme.Current;
            bool inCode = false;
            int printed = 0;
            for (int i = startLine; i < _lines.Length && printed < maxLines; i++)
            {
                var line = _lines[i];
                if (line.StartsWith("```")) { inCode = !inCode; continue; }

                // Draw background rectangle for full line width to clear any previous content
                b.DrawRect(new DL.Rect(x, y + printed, width, 1, theme.Background));

                DL.Rgb24 fg;
                DL.CellAttrFlags attr = DL.CellAttrFlags.None;
                if (!inCode && line.StartsWith("# ")) { line = line.Substring(2); fg = new DL.Rgb24(100, 200, 255); attr = DL.CellAttrFlags.Bold; }
                else if (!inCode && line.StartsWith("## ")) { line = line.Substring(3); fg = new DL.Rgb24(150, 220, 150); attr = DL.CellAttrFlags.Bold; }
                else if (!inCode && line.StartsWith("### ")) { line = line.Substring(4); fg = new DL.Rgb24(255, 180, 100); attr = DL.CellAttrFlags.Bold; }
                else if (inCode) { fg = new DL.Rgb24(180, 180, 180); }
                else { fg = new DL.Rgb24(220, 220, 220); }
                string t = line.Length > width ? line.Substring(0, width) : line;
                b.DrawText(new DL.TextRun(x, y + printed, t, fg, theme.Background, attr));
                printed++;
            }
        }
    }


    /// <summary>
    /// Normalizes markdown before it is rendered into the feed: collapses any run of
    /// blank (or whitespace-only) lines down to a single blank line, and guarantees a
    /// single blank line before every markdown heading. Leading/trailing blanks are
    /// trimmed. This keeps the rendered output from showing multiple consecutive blank
    /// lines and gives headings consistent separation.
    /// </summary>
    public static class FeedMarkdown
    {
        /// <summary>
        /// Neutralizes a dangling (unclosed) ``` code fence. A model that opens a fenced code block
        /// and never closes it makes a CommonMark renderer treat everything to end-of-content as
        /// code, so bold/inline-code markers in the trailing prose render literally. When the count
        /// of fence-delimiter lines is odd, the last one is unmatched; drop that line so the content
        /// after it parses as normal markdown again. Balanced fences are returned unchanged.
        /// </summary>
        internal static string BalanceCodeFences(string md)
        {
            if (string.IsNullOrEmpty(md) || md.IndexOf("```", StringComparison.Ordinal) < 0) return md ?? string.Empty;

            var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var fenceLines = new List<int>();
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    fenceLines.Add(i);

            if (fenceLines.Count == 0 || fenceLines.Count % 2 == 0) return md; // none or balanced

            int unmatched = fenceLines[^1];
            var kept = new List<string>(lines.Length - 1);
            for (int i = 0; i < lines.Length; i++)
                if (i != unmatched) kept.Add(lines[i]);
            return string.Join("\n", kept);
        }

        public static string Normalize(string md)
        {
            if (string.IsNullOrEmpty(md)) return md ?? string.Empty;

            var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var outp = new List<string>(lines.Length);
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    // Collapse: only keep a blank line if something precedes it and the
                    // previous kept line is not already blank.
                    if (outp.Count > 0 && outp[^1].Length != 0) outp.Add("");
                    continue;
                }

                // Headers must not carry trailing punctuation (colons/semicolons) - strip it so
                // titles render clean regardless of what the model emitted.
                var line = IsHeading(raw) ? StripHeadingTrailingPunctuation(raw) : raw;

                // Ensure a blank line before a heading (unless it starts the content).
                if (IsHeading(line) && outp.Count > 0 && outp[^1].Length != 0) outp.Add("");
                outp.Add(line);
            }

            while (outp.Count > 0 && outp[^1].Length == 0) outp.RemoveAt(outp.Count - 1);
            return string.Join("\n", outp);
        }

        private static bool IsHeading(string line)
        {
            var t = line.TrimStart();
            return t.StartsWith("# ") || t.StartsWith("## ") || t.StartsWith("### ")
                || t.StartsWith("#### ") || t.StartsWith("##### ") || t.StartsWith("###### ");
        }

        /// <summary>Remove trailing ':' / ';' (and surrounding spaces) from a heading line.</summary>
        internal static string StripHeadingTrailingPunctuation(string line)
        {
            var trimmed = line.TrimEnd();
            int end = trimmed.Length;
            while (end > 0 && (trimmed[end - 1] == ':' || trimmed[end - 1] == ';' || trimmed[end - 1] == ' '))
                end--;
            return trimmed.Substring(0, end);
        }
    }

    /// <summary>
    /// Deterministic post-processing for markdown/link output that strips the Underline cell
    /// attribute the bundled Andy.Tui renderer emits for emphasized text and OSC8 links, replacing
    /// it with Bold and a distinct link color. The renderer (and the Link widget) underline by
    /// default and expose no toggle, so we render into a throwaway builder and replay its ops into
    /// the real builder, transforming TextRuns as we go. All other ops and attributes are preserved
    /// exactly. This runs per frame for visible items, so the work is a single linear pass.
    /// </summary>
    internal static class MarkdownLinkStyle
    {
        /// <summary>
        /// Run <paramref name="render"/> against a temporary DisplayListBuilder, then copy the
        /// resulting ops into <paramref name="target"/>, converting any TextRun carrying the
        /// Underline (or DoubleUnderline) flag into Bold and recoloring it to <paramref name="linkColor"/>.
        /// </summary>
        public static void RenderWithoutUnderline(DL.DisplayListBuilder target, DL.Rgb24 linkColor, Action<DL.DisplayListBuilder> render)
        {
            var temp = new DL.DisplayListBuilder();
            render(temp);
            CopyOps(temp.Build(), target, linkColor);
        }

        private static void CopyOps(DL.DisplayList list, DL.DisplayListBuilder target, DL.Rgb24 linkColor)
        {
            const DL.CellAttrFlags UnderlineMask = DL.CellAttrFlags.Underline | DL.CellAttrFlags.DoubleUnderline;
            foreach (var op in list.Ops)
            {
                switch (op)
                {
                    case DL.TextRun tr:
                        if ((tr.Attrs & UnderlineMask) != DL.CellAttrFlags.None)
                        {
                            // Drop the underline bits and recolor to the link color WITHOUT adding
                            // bold: emphasis and links stand out by COLOR, not weight, so the feed
                            // isn't flooded with bold. Genuine **bold** runs have no Underline flag
                            // and pass through unchanged below, staying bold.
                            var attrs = tr.Attrs & ~UnderlineMask;
                            var run = new DL.TextRun(tr.X, tr.Y, tr.Content, linkColor, tr.Bg, attrs);
                            target.DrawText(run);
                        }
                        else
                        {
                            var run = tr;
                            target.DrawText(run);
                        }
                        break;
                    case DL.Rect rect:
                        var rectCopy = rect;
                        target.DrawRect(rectCopy);
                        break;
                    case DL.Border border:
                        var borderCopy = border;
                        target.DrawBorder(borderCopy);
                        break;
                    case DL.ClipPush clip:
                        var clipCopy = clip;
                        target.PushClip(clipCopy);
                        break;
                    case DL.LayerPush layer:
                        var layerCopy = layer;
                        target.PushLayer(layerCopy);
                        break;
                    case DL.Pop:
                        target.Pop();
                        break;
                }
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
            _originalMd = (markdown ?? string.Empty).TrimEnd(); // Remove trailing whitespace/newlines
            // Collapse blank-line runs and ensure a blank line before headings.
            // (A former workaround inserted a zero-width non-joiner into the word "You" to dodge a
            // renderer quirk; the current Andy.Tui renderer no longer special-cases "You", and the
            // U+200C rendered as a visible space - "Y ou" - in some terminals, so it was removed.)
            _md = FeedMarkdown.Normalize(_originalMd);

            // Note: Paragraph spacing is handled by Andy.Tui.Widgets.MarkdownRenderer
            // during rendering, so we don't need to do it here
        }

        // Cache the measured display-row count per width. The feed lays out in display rows and
        // calls RenderSlice with display-row indices, so measurement MUST equal what the renderer
        // actually draws. The previous hand-rolled simulation (paragraph-spacing guess + width*0.8
        // wrap estimate) diverged from the real renderer and over-counted, leaving the surplus rows
        // blank at the bottom of every response. We now measure by rendering with the SAME renderer.
        private int _cachedWidth = -1;
        private int _cachedLineCount = -1;

        public int MeasureLineCount(int width)
        {
            if (width <= 0) return 1;
            if (width == _cachedWidth && _cachedLineCount >= 0) return _cachedLineCount;

            int count = MeasureByRendering(width);
            _cachedWidth = width;
            _cachedLineCount = count;
            return count;
        }

        /// <summary>
        /// Build the markdown renderer exactly as RenderSlice does, so measurement and rendering
        /// wrap and space identically. Colors do not affect wrapping, so the result depends only on
        /// the text and width.
        /// </summary>
        private Andy.Tui.Widgets.MarkdownRenderer BuildRenderer()
        {
            var r = new Andy.Tui.Widgets.MarkdownRenderer();
            // Render on the theme background (opaque themes); the renderer defaults to a black
            // background, so without this LLM responses show up on a black block. Transparent
            // themes leave it for the compositor to strip to the terminal bg.
            var theme = Themes.Theme.Current;
            if (theme.Background is { } mdBg)
                r.SetColors(theme.Text, mdBg, theme.Accent);
            // Headers in the feed get the distinct dark-orange heading color (the renderer leaves
            // them at the accent otherwise, which reads the same as links/emphasis).
            r.SetHeaderColors(theme.Heading, theme.Heading, theme.Heading);
            r.SetText(_md);
            return r;
        }

        /// <summary>
        /// The number of display rows the real renderer produces for this width = the row of the
        /// last drawn glyph + 1. Rendered into a throwaway display list with a generous height bound.
        /// </summary>
        private int MeasureByRendering(int width)
        {
            // Upper bound on rendered height: raw lines + a wrap allowance, doubled, with margin.
            // Must exceed the true height (under-counting would clip content) without being wasteful.
            int newlines = 0;
            foreach (var c in _md) if (c == '\n') newlines++;
            int bound = Math.Clamp((newlines + _md.Length / Math.Max(1, width) + 1) * 2 + 32, 64, 8192);

            var probe = new DL.DisplayListBuilder();
            var probeBase = new DL.DisplayListBuilder().Build();
            BuildRenderer().Render(new L.Rect(0, 0, width, bound), probeBase, probe);

            int maxRow = -1;
            foreach (var op in probe.Build().Ops)
            {
                if (op is DL.TextRun tr && !string.IsNullOrEmpty(tr.Content) && tr.Y > maxRow)
                    maxRow = tr.Y;
            }
            return maxRow < 0 ? 1 : maxRow + 1;
        }

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            // Guard against invalid dimensions
            if (width <= 0 || maxLines <= 0) return;
            if (startLine < 0) return;
            if (startLine >= MeasureLineCount(width)) return;

            // Detect a whole-content simple HTML link <a href="...">text</a> and render it as a link.
            if (startLine == 0 && TryRenderSimpleHtmlLink(_md, x, y, width, maxLines, baseDl, b)) return;

            // Render the FULL markdown, shifted up by startLine display rows, clipped to the
            // [y, y+maxLines) window. Because measurement uses this exact renderer, the rows the feed
            // reserved line up with the rows drawn — no slicing by raw line index, no blank surplus.
            int total = MeasureLineCount(width);
            b.PushClip(new DL.ClipPush(x, y, width, maxLines));
            // Render into a throwaway builder, then replay its ops with the Underline attribute
            // (used by the bundled renderer for emphasized text) converted to Bold + link color.
            var linkColor = Themes.Theme.Current.Accent;
            MarkdownLinkStyle.RenderWithoutUnderline(b, linkColor, temp =>
                BuildRenderer().Render(new L.Rect(x, y - startLine, width, total), baseDl, temp));
            b.Pop();
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
            // The Link widget renders its text Bold+Underline; convert the underline to a bold
            // link color so it matches the rest of the no-underline styling.
            var linkColor = Themes.Theme.Current.Accent;
            MarkdownLinkStyle.RenderWithoutUnderline(b, linkColor, temp =>
                link.Render(new L.Rect(x, y, Math.Max(1, width), 1), baseDl, temp));
            return true;
        }
    }

    /// <summary>
    /// Markdown table feed item. Rendered by hand with box-drawing borders and vertical column
    /// separators. The bundled Andy.Tui.Widgets.Table draws only an outer box (no inner vertical
    /// lines) and stretches its content to fill the full row budget it is handed, which produced
    /// tables with no column dividers plus a run of phantom blank rows below them. Rendering here
    /// gives us explicit vertical separators and makes MeasureLineCount equal the rows actually
    /// drawn. Box-drawing glyphs match the style already used by UserBubbleItem; CLAUDE.md's
    /// ASCII-only guidance targets emoji, and existing widgets already use box-drawing borders.
    /// </summary>
    public sealed class TableItem : IFeedItem
    {
        private readonly List<string> _headers;
        private readonly List<string[]> _rows;
        private readonly string? _title;

        public TableItem(List<string> headers, List<string[]> rows, string? title = null)
        {
            _headers = headers;
            _rows = rows;
            _title = title;
        }

        // Layout: top border + header + header separator + N data rows + bottom border.
        // This is exactly the number of rows RenderSlice draws, so the feed reserves no surplus
        // (the old measure over-counted relative to the widget's actual output, leaving a phantom
        // blank line around every table).
        public int MeasureLineCount(int width)
        {
            if (_headers.Count == 0) return 0;
            return _rows.Count + 4;
        }

        /// <summary>
        /// Compute the inner (text) width of each column. Each column is sized to its widest cell
        /// (header or data), then shrunk proportionally if the table would overflow the available
        /// width. One space of padding is added inside every cell on each side.
        /// </summary>
        private int[] ComputeColumnWidths(int width)
        {
            int cols = _headers.Count;
            var content = new int[cols];
            for (int i = 0; i < cols; i++)
            {
                int max = _headers[i]?.Length ?? 0;
                foreach (var row in _rows)
                    if (i < row.Length && row[i] != null)
                        max = Math.Max(max, row[i].Length);
                content[i] = Math.Max(1, max);
            }

            // Total width when each cell is padded by one space on each side:
            // sum(content + 2) inner widths, plus (cols + 1) vertical border glyphs.
            const int pad = 2;
            int borders = cols + 1;
            int needed = content.Sum(c => c + pad) + borders;

            if (needed <= width || width <= borders + (cols * pad))
            {
                // Fits, or width too small to scale meaningfully: use content widths.
                return content;
            }

            // Overflow: shrink columns proportionally, keeping a sensible minimum.
            int innerBudget = Math.Max(cols, width - borders - (cols * pad));
            int totalContent = content.Sum();
            var result = new int[cols];
            for (int i = 0; i < cols; i++)
            {
                double proportion = (double)content[i] / totalContent;
                result[i] = Math.Max(3, (int)(innerBudget * proportion));
            }
            return result;
        }

        private static string Fit(string? text, int innerWidth)
        {
            text ??= string.Empty;
            text = text.Replace("\n", " ").Replace("\r", " ");
            if (text.Length > innerWidth)
                return innerWidth <= 1 ? text.Substring(0, innerWidth) : text.Substring(0, innerWidth - 1) + "…";
            return text.PadRight(innerWidth);
        }

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (width <= 0 || maxLines <= 0) return;
            int total = MeasureLineCount(width);
            if (total == 0 || startLine >= total) return;
            if (startLine < 0) startLine = 0;

            var theme = Themes.Theme.Current;
            var border = theme.Border;
            var headerColor = theme.Heading;
            var textColor = theme.Text;

            int cols = _headers.Count;
            var colInner = ComputeColumnWidths(width);

            // Build the three horizontal rules once (top / header-separator / bottom).
            string Rule(string left, string mid, string right)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(left);
                for (int i = 0; i < cols; i++)
                {
                    sb.Append(new string('─', colInner[i] + 2)); // +2 for cell padding
                    sb.Append(i == cols - 1 ? right : mid);
                }
                return sb.ToString();
            }

            string topRule = Rule("┌", "┬", "┐");
            string midRule = Rule("├", "┼", "┤");
            string botRule = Rule("└", "┴", "┘");

            int end = Math.Min(total, startLine + maxLines);
            for (int line = startLine; line < end; line++)
            {
                int row = y + (line - startLine);

                if (line == 0)
                {
                    b.DrawText(new DL.TextRun(x, row, topRule, border, null, DL.CellAttrFlags.None));
                }
                else if (line == 1)
                {
                    DrawCellRow(b, x, row, colInner, _headers.ToArray(), border, headerColor, DL.CellAttrFlags.Bold);
                }
                else if (line == 2)
                {
                    b.DrawText(new DL.TextRun(x, row, midRule, border, null, DL.CellAttrFlags.None));
                }
                else if (line == total - 1)
                {
                    b.DrawText(new DL.TextRun(x, row, botRule, border, null, DL.CellAttrFlags.None));
                }
                else
                {
                    int dataIdx = line - 3;
                    var cells = dataIdx >= 0 && dataIdx < _rows.Count ? _rows[dataIdx] : Array.Empty<string>();
                    DrawCellRow(b, x, row, colInner, cells, border, textColor, DL.CellAttrFlags.None);
                }
            }
        }

        // Draw a content row "| c1 | c2 | ... |" using vertical separators in the border color and
        // cell text in the supplied color/attrs. The separators are drawn as distinct runs (not
        // baked into one string) so their color stays independent of the cell text color.
        private static void DrawCellRow(DL.DisplayListBuilder b, int x, int row, int[] colInner, string[] cells, DL.Rgb24 borderColor, DL.Rgb24 textColor, DL.CellAttrFlags attrs)
        {
            int cx = x;
            b.DrawText(new DL.TextRun(cx, row, "│", borderColor, null, DL.CellAttrFlags.None));
            cx += 1;
            for (int i = 0; i < colInner.Length; i++)
            {
                string cellText = i < cells.Length ? cells[i] : string.Empty;
                string content = " " + Fit(cellText, colInner[i]) + " ";
                b.DrawText(new DL.TextRun(cx, row, content, textColor, null, attrs));
                cx += content.Length;
                b.DrawText(new DL.TextRun(cx, row, "│", borderColor, null, DL.CellAttrFlags.None));
                cx += 1;
            }
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
            var theme = Themes.Theme.Current;
            var bg = theme.CodeBlockBackground;
            var fg = theme.Text;
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
            // Theme-colored, never underlined: keywords, types/class names, method calls,
            // strings, comments and numbers each get a distinct theme color.
            var palette = SyntaxPalette.FromTheme(Themes.Theme.Current);
            foreach (var (text, color) in CodeHighlighter.Highlight(line, lang, palette))
                yield return (text, color, DL.CellAttrFlags.None);
        }
    }

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


    /// <summary>Tool execution display with dotted yellow line on the left side.</summary>
    public sealed class ToolExecutionItem : IFeedItem
    {
        // How many result lines the expanded preview shows at most. Claude Code shows a
        // larger multi-line preview; we cap it so a huge tool result cannot dominate the feed.
        private const int ExpandedResultPreviewLines = 20;

        private readonly string _toolId;
        private readonly Dictionary<string, object?> _parameters = new();
        private readonly string? _result;
        private readonly bool _isSuccess;
        private readonly string _resultSummary = "";

        // A line plan is the single source of truth shared by MeasureLineCount and
        // RenderSlice: each entry is (text, color-role) so the two never diverge (a
        // mismatch would leave phantom blank rows — see the IFeedItem contract). The
        // plan is rebuilt whenever the width or the expand/collapse mode changes.
        private enum Role { Header, Param, ResultLabel, ResultBody, Dim }
        private List<(string text, Role role)> _plan = new();
        private int _planWidth = -1;
        private bool _planExpanded;

        public ToolExecutionItem(string toolId, Dictionary<string, object?> parameters, string? result = null, bool isSuccess = true)
        {
            _toolId = toolId;
            _parameters = parameters ?? new();
            _result = result;
            _isSuccess = isSuccess;

            // One-line result summary used in collapsed mode.
            if (!string.IsNullOrWhiteSpace(_result))
            {
                if (_toolId == "list_directory" && _result!.Contains("\"items\""))
                {
                    var (count, dirs) = TrySummarizeDirectoryItems(_result!);
                    _resultSummary = count >= 0
                        ? $"{count} entries" + (dirs >= 0 ? $", {dirs} directories" : "")
                        : FirstLine(_result!);
                }
                else
                {
                    _resultSummary = FirstLine(_result!);
                }
            }
        }

        // Status marker: a small asterisk, colored green/red at draw time by success/failure.
        // Kept deliberately small (plain ASCII) so tool call lines stay compact.
        private string StatusMarker => "*";

        private void EnsurePlan(int width)
        {
            bool expanded = ToolOutputView.Expanded;
            if (width == _planWidth && expanded == _planExpanded && _plan.Count > 0) return;

            _planWidth = width;
            _planExpanded = expanded;
            var plan = new List<(string, Role)>();

            // Header (always one line; truncated horizontally at draw). Collapsed mode shows a
            // human-readable action summary ("Reading src/Program.cs") instead of the raw tool
            // id + arguments (#223); the expanded view keeps the tool id and full parameters.
            var header = expanded
                ? _toolId
                : Services.ToolCallSummarizer.Summarize(_toolId, _parameters);
            plan.Add(($"{StatusMarker} {header}", Role.Header));

            if (!expanded)
            {
                // COLLAPSED: the human-readable header already describes the arguments,
                // so only a one-line result summary follows.
                if (!string.IsNullOrEmpty(_resultSummary))
                {
                    plan.Add(("Result: " + _resultSummary, Role.ResultLabel));
                }
            }
            else
            {
                // EXPANDED: full parameters (wrapped) + multi-line result preview.
                if (_parameters.Count > 0)
                {
                    plan.Add(("Parameters:", Role.Dim));
                    foreach (var kv in _parameters)
                    {
                        var value = (kv.Value?.ToString() ?? "null").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                        foreach (var w in TextWrap.Wrap($"  {kv.Key} = {value}", Math.Max(1, width)))
                            plan.Add((w, Role.Param));
                    }
                }
                if (!string.IsNullOrWhiteSpace(_result))
                {
                    plan.Add(("Result:", Role.ResultLabel));
                    var resultLines = _result!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    int emitted = 0;
                    foreach (var rl in resultLines)
                    {
                        if (emitted >= ExpandedResultPreviewLines) break;
                        // Exactly one leading space before output content (#225). The indent is
                        // applied after wrapping because TextWrap.Wrap drops leading spaces.
                        foreach (var w in TextWrap.Wrap(rl, Math.Max(1, width - 1)))
                        {
                            plan.Add((" " + w, Role.ResultBody));
                            if (++emitted >= ExpandedResultPreviewLines) break;
                        }
                    }
                    int totalResultLines = resultLines.Length;
                    if (totalResultLines > emitted)
                        plan.Add(($" ... (+{totalResultLines - emitted} more lines)", Role.Dim));
                }
            }

            _plan = plan;
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
            if (width <= 0) return 1;
            EnsurePlan(width);
            return Math.Max(1, _plan.Count);
        }

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (width <= 0 || maxLines <= 0) return;
            EnsurePlan(width);

            var theme = Themes.Theme.Current;
            var cyan = theme.ToolName;
            var dim = theme.TextDim;

            int drawn = 0;
            for (int i = startLine; i < _plan.Count && drawn < maxLines; i++)
            {
                var (text, role) = _plan[i];
                int row = y + drawn;
                var t = text.Length > width ? text.Substring(0, Math.Max(0, width - 1)) : text;
                switch (role)
                {
                    case Role.Header:
                        // Leading status marker in green/red; the tool id in the tool-name color.
                        if (t.StartsWith("* ", StringComparison.Ordinal))
                        {
                            var dotColor = _isSuccess ? new DL.Rgb24(0, 200, 0) : new DL.Rgb24(200, 0, 0);
                            b.DrawText(new DL.TextRun(x, row, "*", dotColor, null, DL.CellAttrFlags.Bold));
                            b.DrawText(new DL.TextRun(x + 1, row, t.Substring(1), cyan, null, DL.CellAttrFlags.Bold));
                        }
                        else
                        {
                            b.DrawText(new DL.TextRun(x, row, t, cyan, null, DL.CellAttrFlags.Bold));
                        }
                        break;
                    case Role.Param:
                        b.DrawText(new DL.TextRun(x, row, t, dim, null, DL.CellAttrFlags.None));
                        break;
                    case Role.ResultLabel:
                        // Collapsed mode emits a single "Result: <summary>" line. Draw the
                        // label dim and the summary in the ToolResult theme color so the
                        // result text stays visually distinct (and themeable).
                        const string resultPrefix = "Result: ";
                        if (t.StartsWith(resultPrefix, StringComparison.Ordinal) && width > resultPrefix.Length)
                        {
                            b.DrawText(new DL.TextRun(x, row, resultPrefix, dim, null, DL.CellAttrFlags.None));
                            var body = t.Substring(resultPrefix.Length);
                            b.DrawText(new DL.TextRun(x + resultPrefix.Length, row, body, theme.ToolResult, null, DL.CellAttrFlags.None));
                        }
                        else
                        {
                            b.DrawText(new DL.TextRun(x, row, t, dim, null, DL.CellAttrFlags.None));
                        }
                        break;
                    case Role.ResultBody:
                        b.DrawText(new DL.TextRun(x, row, t, theme.ToolResult, null, DL.CellAttrFlags.None));
                        break;
                    default:
                        b.DrawText(new DL.TextRun(x, row, t, dim, null, DL.CellAttrFlags.None));
                        break;
                }
                drawn++;
            }
        }
    }


    /// <summary>Processing indicator with animation.</summary>
    public sealed class ProcessingIndicatorItem : IFeedItem
    {
        private readonly DateTime _startTime;
        private readonly TurnStats? _stats;
        private int _animationFrame;
        private readonly string[] _spinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

        public ProcessingIndicatorItem(TurnStats? stats = null)
        {
            _startTime = DateTime.UtcNow;
            _stats = stats;
        }

        /// <summary>
        /// Compose the live suffix appended to the processing message: elapsed time,
        /// operations performed, and context tokens, separated by " · ". Public/static so the
        /// formatting can be unit-tested without driving the display list.
        /// </summary>
        internal static string BuildStatsSuffix(TimeSpan elapsed, TurnStats? stats)
        {
            var segments = new System.Collections.Generic.List<string>();
            if (elapsed.TotalSeconds >= 1)
                segments.Add($"{elapsed.TotalSeconds:F1}s");
            if (stats != null)
            {
                segments.Add(stats.Operations == 1 ? "1 op" : $"{stats.Operations} ops");
                if (stats.InputTokens > 0)
                    segments.Add($"{TokenFormat.Short(stats.InputTokens)} in");
                if (stats.OutputTokens > 0)
                    segments.Add($"{TokenFormat.Short(stats.OutputTokens)} out");
            }
            return segments.Count == 0 ? string.Empty : " · " + string.Join(" · ", segments);
        }

        public int MeasureLineCount(int width)
        {
            return 2; // Indicator line + 1 blank line for spacing
        }

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (width <= 0 || maxLines <= 0 || startLine > 1) return;

            int row = y;
            int drawn = 0;

            // Line 1: Processing message
            if (drawn < maxLines && startLine <= 0)
            {
                // Update animation frame
                _animationFrame = ((int)(DateTime.UtcNow - _startTime).TotalMilliseconds / 100) % _spinnerFrames.Length;

                var theme = Themes.Theme.Current;
                var dim = new DL.Rgb24(150, 150, 150);
                var spinner = _spinnerFrames[_animationFrame];

                // Build message with spinner plus live stats (elapsed, operations, context tokens)
                var elapsed = DateTime.UtcNow - _startTime;
                var message = $"{spinner} Processing request{BuildStatsSuffix(elapsed, _stats)}";

                // Draw background rectangle for full line width to clear any previous content
                b.DrawRect(new DL.Rect(x, row, width, 1, theme.Background));

                b.DrawText(new DL.TextRun(x, row, message, dim, theme.Background, DL.CellAttrFlags.None));
                row++;
                drawn++;
            }

            // Line 2: Blank line for spacing after spinner
            if (drawn < maxLines && startLine <= 1)
            {
                // Just skip - blank line
                row++;
                drawn++;
            }
        }
    }

    /// <summary>Tool execution item in Claude's clean style.</summary>
    internal sealed class RunningToolItem : IFeedItem
    {
        private readonly string _toolId;
        private readonly string _toolName;
        private readonly DateTime _startTime;
        private bool _isComplete;
        private bool _isSuccess;
        private string _duration = "";
        private int _animationFrame;
        private readonly string[] _spinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        private Dictionary<string, object?> _parameters = new();
        private string? _result;
        private int _linesAdded;
        private int _linesRemoved;
        private string _filePath = "";
        private readonly List<string> _details = new();
        // Width available for rendering the current slice, captured in RenderSlice so
        // result-summary helpers can fill the full feed width instead of a narrow cap.
        private int _availableWidth = 80;

        public string ToolId => _toolId;
        public string ToolName => _toolName;
        public bool IsComplete => _isComplete;
        public Dictionary<string, object?> Parameters => _parameters;

        public RunningToolItem(string toolId, string toolName)
        {
            _toolId = toolId;
            _toolName = toolName;
            _startTime = DateTime.UtcNow;
            _isComplete = false;
            _isSuccess = false;
        }

        public void SetComplete(bool success, string duration)
        {
            _isComplete = true;
            _isSuccess = success;
            _duration = duration;
        }

        public void SetParameters(Dictionary<string, object?> parameters)
        {
            _parameters = parameters;

            // Extract file path if present
            if (parameters != null)
            {
                if (parameters.TryGetValue("file_path", out var path))
                {
                    _filePath = path?.ToString() ?? "";
                }
                else if (parameters.TryGetValue("path", out var altPath))
                {
                    _filePath = altPath?.ToString() ?? "";
                }
            }
        }

        public void SetResult(string? result)
        {
            _result = result;
            // Try to extract statistics from result
            ExtractStatistics(result);
        }

        public void SetStatistics(int linesAdded, int linesRemoved)
        {
            _linesAdded = linesAdded;
            _linesRemoved = linesRemoved;
        }

        public void AddDetail(string detail)
        {
            // Avoid adding duplicate details
            if (!_details.Contains(detail))
            {
                _details.Add(detail);
            }
        }

        private void ExtractStatistics(string? result)
        {
            if (string.IsNullOrEmpty(result)) return;

            // Try to extract line counts for read operations
            if (_toolName.Contains("Read") || _toolName.Contains("read_file"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+lines");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var lines))
                {
                    _linesAdded = lines;
                }
                // Also try to extract file size if available
                var sizeMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+(?:\.\d+)?)\s*([KMG]?B)");
                if (sizeMatch.Success)
                {
                    _details.Add($"Size: {sizeMatch.Groups[1].Value}{sizeMatch.Groups[2].Value}");
                }
            }

            // Extract directory listing statistics
            if (_toolName.Contains("list_directory") || _toolName.Contains("ListDirectory"))
            {
                // Try to parse JSON result for file/directory counts and names
                try
                {
                    if (result.Contains("\"items\"") || result.Contains("\"name\""))
                    {
                        // Extract file and directory names
                        var nameMatches = System.Text.RegularExpressions.Regex.Matches(result, @"""name"":\s*""([^""]+)""");
                        var fileNames = new List<string>();
                        var dirNames = new List<string>();

                        // Count files and directories from JSON
                        var fileCount = System.Text.RegularExpressions.Regex.Matches(result, @"""type"":\s*""file""").Count;
                        var dirCount = System.Text.RegularExpressions.Regex.Matches(result, @"""type"":\s*""directory""").Count;

                        // Extract some file names for preview
                        foreach (System.Text.RegularExpressions.Match nameMatch in nameMatches.Take(10))
                        {
                            var name = nameMatch.Groups[1].Value;
                            // Check if it's likely a directory (ends with / or has no extension)
                            if (name.EndsWith("/") || (!name.Contains(".") && !name.StartsWith(".")))
                            {
                                if (dirNames.Count < 3) dirNames.Add(name.TrimEnd('/'));
                            }
                            else
                            {
                                if (fileNames.Count < 5) fileNames.Add(name);
                            }
                        }

                        // Create summary
                        var summary = $"Found {fileCount} files and {dirCount} directories";
                        _details.Add(summary);

                        // Add sample names if available
                        if (fileNames.Any())
                        {
                            var fileList = string.Join(", ", fileNames.Take(3));
                            if (fileCount > 3) fileList += "...";
                            _details.Add($"Files: {fileList}");
                        }
                        if (dirNames.Any())
                        {
                            var dirList = string.Join(", ", dirNames);
                            if (dirCount > dirNames.Count) dirList += "...";
                            _details.Add($"Dirs: {dirList}");
                        }
                    }
                    else if (result.Contains("files") && result.Contains("directories"))
                    {
                        // Try to extract from plain text
                        var fileMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+files?");
                        var dirMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+director");
                        if (fileMatch.Success && dirMatch.Success)
                        {
                            _details.Add($"Found {fileMatch.Groups[1].Value} files and {dirMatch.Groups[1].Value} directories");
                        }
                    }
                }
                catch { /* Ignore parsing errors */ }
            }

            // Extract code indexing statistics
            if (_toolName.Contains("code_index") || _toolName.Contains("CodeIndex") || _toolName.Contains("index"))
            {
                try
                {
                    // Check if the result is already a detailed message (from UiUpdatingToolExecutor)
                    if (!string.IsNullOrEmpty(result) &&
                        (result.StartsWith("Found ") || result.StartsWith("Structure indexed:") || result.StartsWith("Retrieved hierarchy")))
                    {
                        // Use it directly - it's already detailed from UiUpdatingToolExecutor
                        _details.Add(result);
                    }
                    else
                    {
                        // Legacy regex parsing for backward compatibility
                        var indexedMatch = System.Text.RegularExpressions.Regex.Match(result, @"Indexed\s+(\d+)\s+code\s+files\s+out\s+of\s+(\d+)\s+total");
                        if (indexedMatch.Success)
                        {
                            _details.Add($"Indexed {indexedMatch.Groups[1].Value} code files");
                            _details.Add($"Total {indexedMatch.Groups[2].Value} files in repository");
                        }
                        else
                        {
                            // Look for file counts
                            var fileMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+files?");
                            var codeFileMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+code\s+files?");
                            var classMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+class");
                            var methodMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+method");
                            var lineMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+lines?");

                            var stats = new List<string>();
                            if (codeFileMatch.Success)
                                stats.Add($"{codeFileMatch.Groups[1].Value} code files");
                            else if (fileMatch.Success)
                                stats.Add($"{fileMatch.Groups[1].Value} files");

                            if (classMatch.Success) stats.Add($"{classMatch.Groups[1].Value} classes");
                            if (methodMatch.Success) stats.Add($"{methodMatch.Groups[1].Value} methods");
                            if (lineMatch.Success) stats.Add($"{lineMatch.Groups[1].Value} lines");

                            if (stats.Any())
                            {
                                _details.Add($"Indexed: {string.Join(", ", stats)}");
                            }
                            else
                            {
                                _details.Add("Code repository indexed");
                            }
                        }
                    }

                    // Look for language/file type information
                    var langMatch = System.Text.RegularExpressions.Regex.Match(result, @"Languages?:\s*([^\n]+)");
                    if (langMatch.Success)
                    {
                        _details.Add($"Languages: {langMatch.Groups[1].Value.Trim()}");
                    }
                    else
                    {
                        // Look for file extensions
                        var extensions = System.Text.RegularExpressions.Regex.Matches(result, @"\.(cs|py|js|ts|tsx|jsx|java|cpp|c|h|go|rs|rb|php|swift|kt|scala|clj)\b");
                        var uniqueExts = extensions.Cast<System.Text.RegularExpressions.Match>()
                            .Select(m => m.Value)
                            .Distinct()
                            .Take(8)
                            .ToList();
                        if (uniqueExts.Any())
                        {
                            _details.Add($"File types: {string.Join(", ", uniqueExts)}");
                        }
                    }

                    // Look for framework/technology detection
                    if (result.Contains(".csproj") || result.Contains("dotnet"))
                        _details.Add("Technology: .NET/C#");
                    else if (result.Contains("package.json"))
                        _details.Add("Technology: Node.js/JavaScript");
                    else if (result.Contains("requirements.txt") || result.Contains("setup.py"))
                        _details.Add("Technology: Python");
                    else if (result.Contains("pom.xml") || result.Contains("build.gradle"))
                        _details.Add("Technology: Java");
                }
                catch { /* Ignore parsing errors */ }
            }
        }

        // How many result lines the expanded preview shows at most.
        private const int ExpandedResultPreviewLines = 20;
        // How many result lines the COLLAPSED preview shows for raw-output tools (shell commands,
        // git_diff). A single summary line hid what the command produced; show a few lines instead.
        private const int CollapsedResultPreviewLines = 5;

        // Line-plan classification. The plan is the single source of truth shared by
        // MeasureLineCount and RenderSlice so the reserved row count always equals the
        // rows actually drawn (a mismatch causes phantom blank lines — see IFeedItem).
        // The first line is special-cased at draw time so the spinner/elapsed clock can
        // animate per frame without changing the row count.
        private enum LineKind { HeaderRunning, HeaderDone, Status, Result, Detail, Dim }
        private List<(string text, LineKind kind)> _plan = new();
        private int _planWidth = -1;
        private bool _planExpanded;
        private bool _planComplete;

        private void EnsurePlan(int width)
        {
            bool expanded = ToolOutputView.Expanded;
            // Re-plan whenever width, mode, or completion state changes. (Detail/result
            // mutation also changes content, but those only arrive before completion and
            // the running header line is rebuilt every frame, so this is sufficient.)
            if (width == _planWidth && expanded == _planExpanded && _planComplete == _isComplete && _plan.Count > 0)
                return;

            _planWidth = width;
            _planExpanded = expanded;
            _planComplete = _isComplete;
            var plan = new List<(string, LineKind)>();

            // Header line text is rebuilt per-frame in RenderSlice (spinner/elapsed); here
            // we only reserve the row.
            plan.Add((string.Empty, _isComplete ? LineKind.HeaderDone : LineKind.Status));

            if (!_isComplete)
            {
                // Running: status row + a couple of param/detail rows (collapsed) or all
                // params/details (expanded).
                plan.Add((string.Empty, LineKind.Status)); // "Running... [elapsed]" row

                var realParams = _parameters?.Where(p => !p.Key.StartsWith("__")).ToList() ?? new();
                int paramCap = expanded ? int.MaxValue : 2;
                foreach (var p in realParams.Take(paramCap))
                {
                    var value = TruncateValue(p.Value, expanded ? 200 : 20);
                    foreach (var w in WrapDetail($"  {p.Key}: {value}", width, expanded))
                        plan.Add((w, LineKind.Detail));
                }

                var details = expanded ? _details : _details.TakeLast(2).ToList();
                foreach (var d in details)
                    foreach (var w in WrapDetail("  " + d, width, expanded))
                        plan.Add((w, LineKind.Detail));
            }
            else
            {
                // Completed. Preview the actual result lines for any tool whose result is
                // multi-line (shell commands, git_diff, directory listings, search results, ...) -
                // collapsed up to CollapsedResultPreviewLines, expanded up to
                // ExpandedResultPreviewLines - so a one-word summary no longer hides what the tool
                // produced. Single-line results keep the concise summary line below.
                bool rawOutputTool = _toolName.Contains("bash") || _toolName.Contains("command")
                                     || _toolName.Contains("git_diff");
                bool hasMultiLineResult = !string.IsNullOrWhiteSpace(_result)
                    && _result!.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd().Contains('\n');

                if ((rawOutputTool || hasMultiLineResult) && !string.IsNullOrWhiteSpace(_result))
                {
                    var lines = _result!.Replace("\r\n", "\n").Replace('\r', '\n')
                        .Split('\n')
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();
                    int cap = expanded ? ExpandedResultPreviewLines : CollapsedResultPreviewLines;
                    int emitted = 0;
                    for (int li = 0; li < lines.Count && emitted < cap; li++)
                    {
                        // First line carries the "L" marker (and the error prefix on failure);
                        // continuation lines align under it. Exactly one leading space (#225).
                        string prefix = emitted == 0
                            ? " L " + (!_isSuccess ? "Error: " : "")
                            : "   ";
                        foreach (var w in WrapDetail(prefix + lines[li], width, expanded))
                        {
                            plan.Add((w, emitted == 0 ? LineKind.Result : LineKind.Detail));
                            if (++emitted >= cap) break;
                        }
                    }
                    if (lines.Count > emitted)
                        plan.Add(($"   ... (+{lines.Count - emitted} more lines)", LineKind.Dim));
                }
                else
                {
                    // Always emit exactly one result line so every completed tool renders with the
                    // same structure (header + result). When a tool produces no summary (e.g. an
                    // empty directory listing), fall back to a uniform "done"/"failed" so no tool
                    // "skips a line".
                    string summary = BuildResultSummary();
                    string resultText = string.IsNullOrEmpty(summary)
                        ? " L " + (_isSuccess ? "done" : "failed")
                        : " L " + (!_isSuccess ? "Error: " : "") + summary;
                    foreach (var w in WrapDetail(resultText, width, expanded))
                        plan.Add((w, LineKind.Result));

                    // Additional details (skip first; it usually feeds the summary).
                    int detailCap = expanded ? int.MaxValue : 3;
                    for (int i = 1; i < _details.Count && (i <= detailCap); i++)
                        foreach (var w in WrapDetail("   " + _details[i], width, expanded))
                            plan.Add((w, LineKind.Detail));

                    if (expanded && !string.IsNullOrWhiteSpace(_result))
                    {
                        plan.Add((" Output:", LineKind.Dim));
                        var lines = _result!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                        int emitted = 0;
                        foreach (var rl in lines)
                        {
                            if (emitted >= ExpandedResultPreviewLines) break;
                            // Exactly one leading space before output content (#225). The indent is
                            // applied after wrapping because TextWrap.Wrap drops leading spaces.
                            foreach (var w in TextWrap.Wrap(rl, Math.Max(1, width - 1)))
                            {
                                plan.Add((" " + w, LineKind.Detail));
                                if (++emitted >= ExpandedResultPreviewLines) break;
                            }
                        }
                        if (lines.Length > emitted)
                            plan.Add(($" ... (+{lines.Length - emitted} more lines)", LineKind.Dim));
                    }
                }
            }

            _plan = plan;
        }

        // In expanded mode wrap (never truncate); in collapsed mode keep the old
        // single-line behavior (hard truncate the over-long line). Leading spaces are
        // re-applied after wrapping (TextWrap.Wrap drops them) so gutter prefixes such
        // as " L " keep their single leading space (#225).
        private static List<string> WrapDetail(string text, int width, bool expanded)
        {
            if (expanded)
            {
                int indent = 0;
                while (indent < text.Length && text[indent] == ' ') indent++;
                var pad = text.Substring(0, indent);
                return TextWrap.Wrap(text.Substring(indent), Math.Max(1, width - indent))
                    .Select(w => pad + w)
                    .ToList();
            }
            if (text.Length > width - 2 && width > 5)
                text = text.Substring(0, width - 5) + "...";
            return new List<string> { text };
        }

        public int MeasureLineCount(int width)
        {
            if (width <= 0) return 1;
            EnsurePlan(width);
            return Math.Max(1, _plan.Count);
        }

        /// <summary>
        /// Build the completed-tool result summary text (no marker/indent prefix). This
        /// mirrors the tool-type-specific selection that previously lived inline in
        /// RenderSlice; pulling it into a helper lets EnsurePlan reserve the right number
        /// of rows for it.
        /// </summary>
        private string BuildResultSummary()
        {
            if (_toolName.Contains("Read") || _toolName.Contains("read_file"))
            {
                if (_linesAdded > 0)
                {
                    var s = $"Read {_linesAdded} lines";
                    if (_parameters.ContainsKey("limit")) s += " (truncated)";
                    return s;
                }
                return GetResultSummary();
            }
            if (_toolName.Contains("list_directory") || _toolName.Contains("ListDirectory"))
                return _details.Any() ? _details.First() : GetResultSummary();
            if (_toolName.Contains("code_index") || _toolName.Contains("CodeIndex") || _toolName.Contains("index"))
                return _details.Any() ? _details.First() : GetResultSummary();
            if (_toolName.Contains("Update") || _toolName.Contains("Edit") || _toolName.Contains("Write") ||
                _toolName.Contains("update_file") || _toolName.Contains("edit_file"))
            {
                if (_linesAdded > 0 || _linesRemoved > 0)
                {
                    var s = $"Updated {GetShortPath(_filePath)}";
                    if (_linesAdded > 0 && _linesRemoved > 0) s += $" with {_linesAdded} additions and {_linesRemoved} removals";
                    else if (_linesAdded > 0) s += $" with {_linesAdded} additions";
                    else if (_linesRemoved > 0) s += $" with {_linesRemoved} removals";
                    return s;
                }
                return GetResultSummary();
            }
            return GetResultSummary();
        }

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (width <= 0 || maxLines <= 0) return;

            // Remember the full available width so single-line summaries (e.g. command
            // output) can use the entire feed width instead of a narrow hard-coded cap.
            _availableWidth = width;

            // Advance the spinner; the running header is rendered fresh each frame.
            _animationFrame = (_animationFrame + 1) % _spinnerFrames.Length;

            EnsurePlan(width);

            // Colors
            var white = new DL.Rgb24(255, 255, 255);
            var dim = new DL.Rgb24(150, 150, 150);
            var dimmer = new DL.Rgb24(100, 100, 100);
            var green = new DL.Rgb24(0, 200, 0);
            var red = new DL.Rgb24(200, 0, 0);
            var orange = new DL.Rgb24(255, 165, 0);

            int drawn = 0;
            for (int i = startLine; i < _plan.Count && drawn < maxLines; i++)
            {
                int row = y + drawn;
                var (text, kind) = _plan[i];

                // The first reserved row (index 0) is the header and is composed live so
                // the spinner / elapsed clock animate without changing the row count.
                if (i == 0)
                {
                    // Collapsed mode shows a human-readable action summary ("Reading
                    // src/Program.cs") instead of the raw "tool_name(args)" line (#223).
                    // The expanded view keeps the tool name + raw arguments.
                    string HeaderBody()
                        => ToolOutputView.Expanded
                            ? BuildRawHeaderBody()
                            : Services.ToolCallSummarizer.Summarize(_toolName, _parameters);

                    if (!_isComplete)
                    {
                        var spinner = _spinnerFrames[_animationFrame];
                        var toolDisplay = $"{spinner} {HeaderBody()}";
                        if (toolDisplay.Length > width) toolDisplay = toolDisplay.Substring(0, Math.Max(0, width - 1));
                        b.DrawText(new DL.TextRun(x, row, toolDisplay, white, null, DL.CellAttrFlags.None));
                    }
                    else
                    {
                        // Completion marker: a small asterisk colored green (success) / red (failure),
                        // orange for a successful-with-warning result. Plain ASCII, kept small so
                        // tool call lines stay compact.
                        var symbolColor = _isSuccess ? green : red;
                        if (_isSuccess && !string.IsNullOrEmpty(_result) &&
                            (_result.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                             _result.Contains("partial", StringComparison.OrdinalIgnoreCase)))
                            symbolColor = orange;

                        const string marker = "*"; // green/red status marker
                        b.DrawText(new DL.TextRun(x, row, marker, symbolColor, null, DL.CellAttrFlags.None));

                        var toolDisplay = $" {HeaderBody()}";
                        int tx = x + marker.Length;
                        if (toolDisplay.Length > width - marker.Length) toolDisplay = toolDisplay.Substring(0, Math.Max(0, width - marker.Length - 1));
                        b.DrawText(new DL.TextRun(tx, row, toolDisplay, white, null, DL.CellAttrFlags.None));
                    }
                    drawn++;
                    continue;
                }

                // Index 1 of a running tool is the live "Running... [elapsed]" status row.
                if (!_isComplete && i == 1)
                {
                    var elapsed = DateTime.UtcNow - _startTime;
                    var statusText = $" L Running... [{FormatDuration(elapsed)}]";
                    if (statusText.Length > width) statusText = statusText.Substring(0, Math.Max(0, width - 1));
                    b.DrawText(new DL.TextRun(x, row, statusText, dimmer, null, DL.CellAttrFlags.None));
                    drawn++;
                    continue;
                }

                var t = text.Length > width ? text.Substring(0, Math.Max(0, width - 1)) : text;
                DL.Rgb24 color = kind switch
                {
                    LineKind.Result => dim,
                    LineKind.Detail => dimmer,
                    LineKind.Dim => dimmer,
                    _ => dim,
                };
                b.DrawText(new DL.TextRun(x, row, t, color, null, DL.CellAttrFlags.None));
                drawn++;
            }
        }

        private string GetShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            // Shorten long paths
            var parts = path.Split('/');
            if (parts.Length > 3)
            {
                return $".../{parts[parts.Length - 2]}/{parts[parts.Length - 1]}";
            }
            return path;
        }

        private string GetResultSummary()
        {
            // Try to provide more specific summaries based on tool type
            if (_toolName.Contains("list_directory") || _toolName.Contains("ListDirectory"))
            {
                // Check for error first, before checking details
                if (!_isSuccess && !string.IsNullOrEmpty(_result))
                    return FirstLine(_result);

                if (_details.Any())
                    return _details.First();
                else if (!string.IsNullOrEmpty(_result))
                {
                    return ExtractDirectoryStats(_result);
                }
                else if (!string.IsNullOrEmpty(_filePath))
                    return $"Listed {GetShortPath(_filePath)}";
                else
                    return ""; // Don't show generic message
            }
            else if (_toolName.Contains("code_index") || _toolName.Contains("CodeIndex"))
            {
                // Check for error first, before checking details
                if (!_isSuccess && !string.IsNullOrEmpty(_result))
                    return FirstLine(_result);

                if (_details.Any())
                    return _details.First();
                else if (!string.IsNullOrEmpty(_result))
                {
                    return ExtractCodeIndexStats(_result);
                }
                else
                    return ""; // Don't show generic message
            }
            else if (_toolName.Contains("bash") || _toolName.Contains("command"))
            {
                // Show the actual command output (collapsed: first non-empty line; the full output
                // is shown in the expanded view). A bare line count hid what the command produced.
                if (!string.IsNullOrWhiteSpace(_result))
                    return FirstNonEmptyLine(_result);
                return _isSuccess ? "done" : "failed";
            }
            else if (!string.IsNullOrEmpty(_result))
            {
                return FirstLine(_result);
            }
            else if (_isSuccess)
            {
                // Try to show something more meaningful than generic message
                if (_parameters != null)
                {
                    // Show the operation type if available
                    if (_parameters.TryGetValue("operation", out var op) && op != null)
                    {
                        return $"Completed: {op}";
                    }
                    if (_parameters.TryGetValue("action", out var action) && action != null)
                    {
                        return $"Completed: {action}";
                    }
                }
                return "Done";  // Much shorter than "Completed successfully"
            }
            else
            {
                return "Failed";
            }
        }

        private string ExtractDirectoryStats(string result)
        {
            // Check for empty directory message
            if (result.Contains("empty", StringComparison.OrdinalIgnoreCase))
            {
                return result; // Return the message as-is (e.g., "Directory is empty")
            }

            // Try to extract meaningful stats from result
            var fileCount = System.Text.RegularExpressions.Regex.Matches(result, @"""type"":\s*""file""").Count;
            var dirCount = System.Text.RegularExpressions.Regex.Matches(result, @"""type"":\s*""directory""").Count;

            if (fileCount > 0 || dirCount > 0)
            {
                return $"Found {fileCount} files and {dirCount} directories";
            }

            // Try to count items in array
            var itemMatches = System.Text.RegularExpressions.Regex.Matches(result, @"\{[^}]+\}");
            if (itemMatches.Count > 0)
            {
                return $"Listed {itemMatches.Count} items";
            }

            return "Directory contents retrieved";
        }

        private string ExtractCodeIndexStats(string result)
        {
            // Try to extract meaningful stats from code index result
            var fileMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+files?");
            var classMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+class");
            var functionMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+(function|method)");
            var lineMatch = System.Text.RegularExpressions.Regex.Match(result, @"(\d+)\s+lines?");

            var stats = new List<string>();
            if (fileMatch.Success) stats.Add($"{fileMatch.Groups[1].Value} files");
            if (classMatch.Success) stats.Add($"{classMatch.Groups[1].Value} classes");
            if (functionMatch.Success) stats.Add($"{functionMatch.Groups[1].Value} {functionMatch.Groups[2].Value}s");
            if (lineMatch.Success) stats.Add($"{lineMatch.Groups[1].Value} lines");

            if (stats.Any())
            {
                return $"Indexed: {string.Join(", ", stats.Take(3))}";
            }

            return "Code repository indexed";
        }

        private static string TruncateValue(object? value, int maxLen = 20)
        {
            var s = value?.ToString() ?? "null";
            if (s.Length > maxLen) s = s.Substring(0, maxLen - 3) + "...";
            return s.Replace("\n", " ").Replace("\r", " ").Trim();
        }

        private string FirstLine(string s)
        {
            var line = s.Replace("\r\n", "\n").Replace('\r', '\n');
            var nl = line.IndexOf('\n');
            if (nl >= 0) line = line.Substring(0, nl);
            // Fill the available feed width rather than a narrow hard-coded cap so wide
            // command output is not needlessly clipped to ~1/3 of the terminal. The final
            // render also clips to the exact width; this keeps the content budget aligned
            // with the available space. Reserve a few columns for the " L " gutter/prefix.
            int budget = Math.Max(40, _availableWidth - 4);
            if (line.Length > budget) line = line.Substring(0, Math.Max(0, budget - 3)) + "...";
            return line.Trim();
        }

        // First line that has visible content (commands sometimes emit a leading blank line).
        private string FirstNonEmptyLine(string s)
        {
            foreach (var raw in s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                if (!string.IsNullOrWhiteSpace(raw))
                    return FirstLine(raw);
            return FirstLine(s);
        }

        /// <summary>
        /// The raw "tool_name(args)" header body, shown in the expanded view so the exact
        /// tool id and arguments stay available next to the human-readable collapsed summary.
        /// </summary>
        private string BuildRawHeaderBody()
        {
            var paramDisplay = GetParameterDisplay();
            return _toolName + (string.IsNullOrEmpty(paramDisplay) ? "()" : $"({paramDisplay})");
        }

        private string GetParameterDisplay()
        {
            // Skip internal parameters starting with __
            var realParams = _parameters?.Where(p => !p.Key.StartsWith("__")).ToList();

            // If no real parameters yet, show loading
            if (realParams == null || !realParams.Any())
            {
                // For file-based operations that set _filePath directly
                if (!string.IsNullOrEmpty(_filePath))
                    return GetShortPath(_filePath);

                // While running, show a waiting hint. Once complete, real params never arrived
                // (a tracking miss) - show nothing rather than a stale "loading...".
                return IsComplete ? "" : "loading...";
            }

            var displayParams = new List<string>();

            // Special handling for datetime_tool
            if (_toolName.Contains("datetime", StringComparison.OrdinalIgnoreCase))
            {
                // Show the operation being performed
                var opParam = realParams.FirstOrDefault(p => p.Key.Contains("operation", StringComparison.OrdinalIgnoreCase));
                if (opParam.Value != null)
                {
                    displayParams.Add($"op:{opParam.Value}");
                }

                // Show timezone if specified
                var tzParam = realParams.FirstOrDefault(p => p.Key.Contains("timezone", StringComparison.OrdinalIgnoreCase));
                if (tzParam.Value != null)
                {
                    displayParams.Add($"tz:{tzParam.Value}");
                }
            }
            // Special handling for different tools
            else if (_toolName.Contains("code_index", StringComparison.OrdinalIgnoreCase) ||
                     _toolName.Contains("index", StringComparison.OrdinalIgnoreCase))
            {
                var parts = new List<string>();

                // Look for any path-like parameter
                var pathParam = realParams.FirstOrDefault(p => p.Key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                                                               p.Key.Contains("dir", StringComparison.OrdinalIgnoreCase));
                if (pathParam.Value != null)
                {
                    parts.Add(GetShortPath(pathParam.Value.ToString() ?? ""));
                }

                // Show query if searching for something specific
                var queryParam = realParams.FirstOrDefault(p => p.Key.Contains("query", StringComparison.OrdinalIgnoreCase));
                if (queryParam.Value != null)
                {
                    var queryStr = queryParam.Value.ToString() ?? "";
                    if (queryStr.Length > 30) queryStr = queryStr.Substring(0, 27) + "...";
                    parts.Add($"query: '{queryStr}'");
                }

                // Show namespace or class filter
                if (_parameters != null)
                {
                    if (_parameters.TryGetValue("namespace", out var ns) && ns != null)
                    {
                        parts.Add($"namespace: {ns}");
                    }
                    else if (_parameters.TryGetValue("class", out var cls) && cls != null)
                    {
                        parts.Add($"class: {cls}");
                    }
                }

                // Show type filter
                if (_parameters != null && _parameters.TryGetValue("type", out var type) && type != null)
                {
                    parts.Add($"type: {type}");
                }

                return parts.Any() ? string.Join(", ", parts) : "current directory";
            }
            else if (_toolName.Contains("list_directory", StringComparison.OrdinalIgnoreCase))
            {
                // Look for any directory/path parameter
                var dirParam = realParams.FirstOrDefault(p => p.Key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                                                              p.Key.Contains("dir", StringComparison.OrdinalIgnoreCase));

                if (dirParam.Value != null)
                {
                    var pathStr = dirParam.Value.ToString() ?? ".";

                    // Build parameter list with significant options
                    var options = new List<string>();

                    var recParam = realParams.FirstOrDefault(p => p.Key.Contains("recursive", StringComparison.OrdinalIgnoreCase));
                    if (recParam.Value?.ToString() == "True")
                        options.Add("recursive");

                    var hiddenParam = realParams.FirstOrDefault(p => p.Key.Contains("hidden", StringComparison.OrdinalIgnoreCase));
                    if (hiddenParam.Value?.ToString() == "True")
                        options.Add("hidden");

                    var patternParam = realParams.FirstOrDefault(p => p.Key.Contains("pattern", StringComparison.OrdinalIgnoreCase));
                    if (patternParam.Value != null)
                        options.Add($"pattern: {patternParam.Value}");

                    var optionsStr = options.Any() ? ", " + string.Join(", ", options) : "";
                    return $"{GetShortPath(pathStr)}{optionsStr}";
                }

                // Fallback if no path found
                return "current directory";
            }
            else if (_toolName.Contains("read_file", StringComparison.OrdinalIgnoreCase))
            {
                // Look for file path parameter
                var fileParam = realParams.FirstOrDefault(p => p.Key.Contains("file", StringComparison.OrdinalIgnoreCase) ||
                                                               p.Key.Contains("path", StringComparison.OrdinalIgnoreCase));
                if (fileParam.Value != null)
                {
                    return GetShortPath(fileParam.Value.ToString() ?? "");
                }
            }
            else if (_toolName.Contains("write_file", StringComparison.OrdinalIgnoreCase))
            {
                // Look for file path parameter (don't show content for privacy)
                var fileParam = realParams.FirstOrDefault(p => p.Key.Contains("file", StringComparison.OrdinalIgnoreCase) ||
                                                               p.Key.Contains("path", StringComparison.OrdinalIgnoreCase));
                if (fileParam.Value != null)
                {
                    return GetShortPath(fileParam.Value.ToString() ?? "");
                }
            }
            else if (_toolName.Contains("delete_file", StringComparison.OrdinalIgnoreCase))
            {
                // Look for file path parameter
                var fileParam = realParams.FirstOrDefault(p => p.Key.Contains("file", StringComparison.OrdinalIgnoreCase) ||
                                                               p.Key.Contains("path", StringComparison.OrdinalIgnoreCase));
                if (fileParam.Value != null)
                {
                    return GetShortPath(fileParam.Value.ToString() ?? "");
                }
            }

            // Generic fallback - show ALL real parameters if no special handling matched
            if (!displayParams.Any() && realParams.Any())
            {
                foreach (var param in realParams.Take(3))
                {
                    var value = TruncateValue(param.Value, 20);
                    displayParams.Add($"{param.Key}={value}");
                }
            }

            return displayParams.Any() ? string.Join(", ", displayParams) : "";
        }

        private static string FormatDuration(TimeSpan elapsed)
        {
            if (elapsed.TotalMilliseconds < 1000)
                return $"{elapsed.TotalMilliseconds:F0}ms";
            else if (elapsed.TotalSeconds < 60)
                return $"{elapsed.TotalSeconds:F1}s";
            else
                return $"{elapsed.TotalMinutes:F1}m";
        }
    }
}
