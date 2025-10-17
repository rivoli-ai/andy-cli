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
        /// <summary>Convenience: append markdown using Andy.Tui.Widgets.MarkdownRenderer to better handle inline formatting. Detects and renders markdown tables separately.</summary>
        public void AddMarkdownRich(string md)
        {
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
        /// <summary>Append a user message bubble with a rounded frame and label.</summary>
        public void AddUserMessage(string text)
        {
            // Add spacing before user messages for better readability
            AddItem(new SpacerItem(1));
            AddItem(new UserBubbleItem(text));
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
        public void AddProcessingIndicator()
        {
            AddItem(new ProcessingIndicatorItem());
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
            // No background rectangle - use transparent terminal background
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
                if (parameters?.TryGetValue("path", out var path) == true)
                {
                    var dirName = Path.GetFileName(path?.ToString() ?? ".") ?? ".";
                    if (string.IsNullOrEmpty(dirName)) dirName = ".";

                    // Try to extract entry counts from result
                    if (resultData is Dictionary<string, object?> resultDict &&
                        resultDict.TryGetValue("entries", out var entries) && entries is IEnumerable<object> entryList)
                    {
                        var entryArray = entryList.ToArray();
                        var fileCount = 0;
                        var dirCount = 0;

                        foreach (var entry in entryArray)
                        {
                            if (entry is Dictionary<string, object?> entryDict &&
                                entryDict.TryGetValue("type", out var type))
                            {
                                if ("file".Equals(type?.ToString(), StringComparison.OrdinalIgnoreCase))
                                    fileCount++;
                                else if ("directory".Equals(type?.ToString(), StringComparison.OrdinalIgnoreCase))
                                    dirCount++;
                            }
                        }

                        resultSummary = $"Listed {dirName}: {fileCount} files, {dirCount} directories";
                    }
                    else
                    {
                        resultSummary = $"Listed {dirName}";
                    }
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
                        if (resultDict.TryGetValue("exit_code", out var exitCode))
                        {
                            resultSummary = exitCode?.ToString() == "0"
                                ? $"Executed {cmdName} successfully"
                                : $"Executed {cmdName} (exit code: {exitCode})";
                        }
                        else if (resultDict.TryGetValue("output", out var output) && output != null)
                        {
                            var outputStr = output.ToString() ?? "";
                            var lines = outputStr.Split('\n').Length;
                            resultSummary = $"Executed {cmdName} ({lines} lines output)";
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

                    // Extract headers
                    var headers = headerLine.Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Select(h => CleanCellContent(h))
                        .ToList();

                    if (headers.Count > 0)
                    {
                        // Extract rows
                        var rows = new List<string[]>();
                        i += 2; // Skip header and separator

                        while (i < lines.Length && lines[i].Contains('|'))
                        {
                            var cells = lines[i].Split('|', StringSplitOptions.RemoveEmptyEntries)
                                .Select(c => CleanCellContent(c))
                                .ToArray();

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

    /// <summary>Markdown table feed item using Andy.Tui.Widgets.Table.</summary>
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

        public int MeasureLineCount(int width)
        {
            // Table needs: 1 for header + 1 for separator + N data rows + 2 for borders
            return _rows.Count + 4;
        }

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (width <= 0 || maxLines <= 0) return;

            var table = new Andy.Tui.Widgets.Table();
            table.SetColumns(_headers.ToArray());

            // Calculate column widths based on actual content
            var minWidths = new int[_headers.Count];
            for (int i = 0; i < _headers.Count; i++)
            {
                // Start with header width
                int maxWidth = _headers[i].Length;

                // Check all row data for this column
                foreach (var row in _rows)
                {
                    if (i < row.Length)
                    {
                        maxWidth = Math.Max(maxWidth, row[i].Length);
                    }
                }

                // Add padding and clamp to reasonable min/max
                minWidths[i] = Math.Clamp(maxWidth + 2, 8, 50);
            }

            table.SetMinColumnWidths(minWidths);

            // Add rows
            table.SetRows(_rows);

            // Render the table
            table.Render(new L.Rect(x, y, width, maxLines), baseDl, b);
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
                    b.DrawText(new DL.TextRun(x, row, "╭" + new string('─', inner) + "╮", borderColor, null, DL.CellAttrFlags.None));
                }
                else if (i == total - 1)
                {
                    // bottom border with rounded corners
                    int inner = Math.Max(0, width - 2);
                    b.DrawText(new DL.TextRun(x, row, "╰" + new string('─', inner) + "╯", borderColor, null, DL.CellAttrFlags.None));
                }
                else
                {
                    // content line with side borders
                    string content = _lines[i - 1];
                    if (i == 1)
                    {
                        // show label on first content row
                        string label = "You:";
                        b.DrawText(new DL.TextRun(x + 2, row, label + " ", labelColor, null, DL.CellAttrFlags.Bold));
                        int available = Math.Max(0, width - 4 - (label.Length + 1));
                        string t = available > 0 ? (content.Length > available ? content.Substring(0, available) : content) : string.Empty;
                        b.DrawText(new DL.TextRun(x + 2 + label.Length + 1, row, t, new DL.Rgb24(220, 220, 220), null, DL.CellAttrFlags.None));
                    }
                    else
                    {
                        int available = Math.Max(0, width - 4);
                        string t = content.Length > available ? content.Substring(0, available) : content;
                        b.DrawText(new DL.TextRun(x + 2, row, t, new DL.Rgb24(220, 220, 220), null, DL.CellAttrFlags.None));
                    }
                    if (width >= 1) b.DrawText(new DL.TextRun(x, row, "│", borderColor, null, DL.CellAttrFlags.None));
                    if (width >= 2) b.DrawText(new DL.TextRun(x + width - 1, row, "│", borderColor, null, DL.CellAttrFlags.None));
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

            // Don't show cursor at all - it's distracting and ugly
            // Cursor should only appear when user is actually typing in an input field
        }
    }

    /// <summary>Processing indicator with animation.</summary>
    public sealed class ProcessingIndicatorItem : IFeedItem
    {
        private readonly DateTime _startTime;
        private int _animationFrame;
        private readonly string[] _spinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

        public ProcessingIndicatorItem()
        {
            _startTime = DateTime.UtcNow;
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

                // Calculate elapsed time
                var elapsed = DateTime.UtcNow - _startTime;
                var elapsedText = elapsed.TotalSeconds < 1 ? "" : $" [{elapsed.TotalSeconds:F1}s]";

                // Build message with spinner
                var message = $"{spinner} Processing request{elapsedText}";

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

            // DEBUG: Write to a file what parameters we received
            try
            {
                var debugPath = "/tmp/tool_params_debug.txt";
                var debugInfo = $"[{DateTime.Now:HH:mm:ss.fff}] SetParameters called for {_toolName}:\n";
                debugInfo += $"  Tool ID: {_toolId}\n";
                debugInfo += $"  Parameter count: {parameters?.Count ?? 0}\n";
                if (parameters != null)
                {
                    foreach (var p in parameters)
                    {
                        debugInfo += $"    {p.Key} = {p.Value}\n";
                    }
                }
                debugInfo += "\n";
                System.IO.File.AppendAllText(debugPath, debugInfo);
            }
            catch { }

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

        public int MeasureLineCount(int width)
        {
            if (!_isComplete)
            {
                // Running: spinner + details
                return 2 + Math.Min(_details.Count, 2); // Show up to 2 detail lines
            }

            // Calculate lines based on content
            int lines = 2; // Tool name + result summary

            // Add lines for statistics if present
            if (_linesAdded > 0 || _linesRemoved > 0)
            {
                lines++; // Statistics line
            }

            // Add detail lines
            lines += Math.Min(_details.Count, 3); // Show up to 3 detail lines when complete

            return Math.Min(lines, 6); // Cap at 6 lines max
        }

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (width <= 0 || maxLines <= 0) return;

            int row = y;
            int drawn = 0;

            // Update animation frame
            _animationFrame = (_animationFrame + 1) % _spinnerFrames.Length;

            // Colors
            var white = new DL.Rgb24(255, 255, 255);
            var dim = new DL.Rgb24(150, 150, 150);
            var dimmer = new DL.Rgb24(100, 100, 100);

            if (!_isComplete)
            {
                // While running: show spinner with tool name
                if (drawn < maxLines && startLine <= 0)
                {
                    var spinner = _spinnerFrames[_animationFrame];
                    var toolDisplay = $"{spinner} {_toolName}";

                    // Show parameters in parentheses
                    var paramDisplay = GetParameterDisplay();
                    if (!string.IsNullOrEmpty(paramDisplay))
                    {
                        toolDisplay += $"({paramDisplay})";
                    }
                    else
                    {
                        toolDisplay += "()";
                    }

                    b.DrawText(new DL.TextRun(x, row, toolDisplay, white, null, DL.CellAttrFlags.None));
                    row++; drawn++;
                }

                // Indented progress indicator
                if (drawn < maxLines && startLine <= 1)
                {
                    var elapsed = DateTime.UtcNow - _startTime;
                    var statusText = $"  ⎿  Running... [{FormatDuration(elapsed)}]";
                    b.DrawText(new DL.TextRun(x, row, statusText, dimmer, null, DL.CellAttrFlags.None));
                    row++; drawn++;
                }

                // Show parameters during execution
                if (_parameters != null && _parameters.Any() && drawn < maxLines)
                {
                    foreach (var param in _parameters.Take(2))
                    {
                        if (drawn >= maxLines || startLine > drawn + 2) break;
                        var paramText = $"      {param.Key}: {TruncateValue(param.Value, 20)}";
                        if (paramText.Length > width - 2)
                        {
                            paramText = paramText.Substring(0, width - 5) + "...";
                        }
                        b.DrawText(new DL.TextRun(x, row, paramText, dimmer, null, DL.CellAttrFlags.None));
                        row++; drawn++;
                    }
                }

                // Show recent details during execution
                var recentDetails = _details.TakeLast(2).ToList();
                for (int i = 0; i < recentDetails.Count && drawn < maxLines; i++)
                {
                    if (startLine <= drawn + 2)
                    {
                        var detailText = $"      {recentDetails[i]}";
                        if (detailText.Length > width - 2)
                        {
                            detailText = detailText.Substring(0, width - 5) + "...";
                        }
                        b.DrawText(new DL.TextRun(x, row, detailText, dimmer, null, DL.CellAttrFlags.None));
                        row++; drawn++;
                    }
                }
            }
            else
            {
                // Completed: show in Claude's style with colored status dots
                // Line 1: Tool name with colored symbol
                if (drawn < maxLines && startLine <= 0)
                {
                    // Use colored dot based on success status
                    var green = new DL.Rgb24(0, 200, 0);
                    var red = new DL.Rgb24(200, 0, 0);
                    var orange = new DL.Rgb24(255, 165, 0);

                    var symbol = "⏺"; // Circle bullet like Claude uses
                    var symbolColor = _isSuccess ? green : red;

                    // Check for warnings (partial success cases)
                    if (_isSuccess && !string.IsNullOrEmpty(_result) &&
                        (_result.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                         _result.Contains("partial", StringComparison.OrdinalIgnoreCase)))
                    {
                        symbolColor = orange;
                    }

                    // Draw the colored symbol separately
                    b.DrawText(new DL.TextRun(x, row, symbol, symbolColor, null, DL.CellAttrFlags.None));

                    // Then draw the tool name and parameters
                    var toolDisplay = $" {_toolName}";

                    // Show parameters in parentheses
                    var paramDisplay = GetParameterDisplay();
                    if (!string.IsNullOrEmpty(paramDisplay))
                    {
                        toolDisplay += $"({paramDisplay})";
                    }
                    else
                    {
                        toolDisplay += "()";
                    }

                    b.DrawText(new DL.TextRun(x + 2, row, toolDisplay, white, null, DL.CellAttrFlags.None));
                    row++; drawn++;
                }

                // Line 2: Result summary with statistics (only show if we have something meaningful)
                if (drawn < maxLines && startLine <= 1)
                {
                    string resultContent = "";

                    // Add specific result based on tool type
                    if (_toolName.Contains("Read") || _toolName.Contains("read_file"))
                    {
                        if (_linesAdded > 0)
                        {
                            resultContent = $"Read {_linesAdded} lines";
                            if (_parameters.ContainsKey("limit"))
                            {
                                resultContent += " (truncated)";
                            }
                        }
                        else
                        {
                            resultContent = GetResultSummary();
                        }
                    }
                    else if (_toolName.Contains("list_directory") || _toolName.Contains("ListDirectory"))
                    {
                        // Show directory listing summary
                        if (_details.Any())
                        {
                            resultContent = _details.First();
                        }
                        else
                        {
                            resultContent = GetResultSummary();
                        }
                    }
                    else if (_toolName.Contains("code_index") || _toolName.Contains("CodeIndex") || _toolName.Contains("index"))
                    {
                        // Show code index summary
                        if (_details.Any())
                        {
                            resultContent = _details.First();
                        }
                        else
                        {
                            resultContent = GetResultSummary(); // This returns empty string now, not generic message
                        }
                    }
                    else if (_toolName.Contains("Update") || _toolName.Contains("Edit") || _toolName.Contains("Write") ||
                             _toolName.Contains("update_file") || _toolName.Contains("edit_file"))
                    {
                        if (_linesAdded > 0 || _linesRemoved > 0)
                        {
                            resultContent = $"Updated {GetShortPath(_filePath)}";
                            if (_linesAdded > 0 && _linesRemoved > 0)
                            {
                                resultContent += $" with {_linesAdded} additions and {_linesRemoved} removals";
                            }
                            else if (_linesAdded > 0)
                            {
                                resultContent += $" with {_linesAdded} additions";
                            }
                            else if (_linesRemoved > 0)
                            {
                                resultContent += $" with {_linesRemoved} removals";
                            }
                        }
                        else
                        {
                            resultContent = GetResultSummary();
                        }
                    }
                    else
                    {
                        resultContent = GetResultSummary();
                    }

                    // Only show the result line if we have actual content
                    if (!string.IsNullOrEmpty(resultContent))
                    {
                        string resultText = "  ⎿  ";

                        if (!_isSuccess)
                        {
                            resultText += "Error: ";
                        }

                        resultText += resultContent;

                        if (resultText.Length > width - 2)
                        {
                            resultText = resultText.Substring(0, width - 5) + "...";
                        }

                        b.DrawText(new DL.TextRun(x, row, resultText, dim, null, DL.CellAttrFlags.None));
                        row++; drawn++;
                    }
                }

                // Show additional details after completion (skip the first one as it's in the summary)
                for (int i = 1; i < _details.Count && i <= 3 && drawn < maxLines; i++)
                {
                    if (startLine <= drawn + 2)
                    {
                        var detailText = $"        {_details[i]}";
                        if (detailText.Length > width - 2)
                        {
                            detailText = detailText.Substring(0, width - 5) + "...";
                        }
                        b.DrawText(new DL.TextRun(x, row, detailText, new DL.Rgb24(100, 100, 100), null, DL.CellAttrFlags.None));
                        row++; drawn++;
                    }
                }

                // Line 3+: Show code diff preview if applicable (for Update/Edit tools)
                if ((_toolName.Contains("Update") || _toolName.Contains("Edit")) && drawn < maxLines && _result != null)
                {
                    // Show a few lines of the diff if available
                    var lines = _result.Split('\n').Take(3);
                    foreach (var line in lines)
                    {
                        if (drawn >= maxLines || startLine > drawn + 2) break;

                        var diffLine = "        " + line.Trim();
                        if (diffLine.Length > width - 2)
                        {
                            diffLine = diffLine.Substring(0, width - 5) + "...";
                        }

                        var lineColor = dimmer;
                        if (line.StartsWith("+")) lineColor = new DL.Rgb24(100, 180, 100);
                        else if (line.StartsWith("-")) lineColor = new DL.Rgb24(180, 100, 100);

                        b.DrawText(new DL.TextRun(x, row, diffLine, lineColor, null, DL.CellAttrFlags.None));
                        row++; drawn++;
                    }
                }
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
                if (!string.IsNullOrEmpty(_result))
                {
                    // Check if this is an error result (failed execution)
                    if (!_isSuccess)
                        return FirstLine(_result);
                    var lines = _result.Split('\n').Length;
                    return $"Command executed ({lines} lines output)";
                }
                return "Command executed";
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

        private static string FirstLine(string s)
        {
            var line = s.Replace("\r\n", "\n").Replace('\r', '\n');
            var nl = line.IndexOf('\n');
            if (nl >= 0) line = line.Substring(0, nl);
            if (line.Length > 80) line = line.Substring(0, 77) + "...";
            return line.Trim();
        }

        private string GetParameterDisplay()
        {
            // DEBUG: Log what parameters we have
            try
            {
                var debugPath = "/tmp/tool_params_debug.txt";
                var debugInfo = $"[{DateTime.Now:HH:mm:ss.fff}] GetParameterDisplay for {_toolName}:\n";
                debugInfo += $"  _parameters is null? {_parameters == null}\n";
                debugInfo += $"  _parameters count: {_parameters?.Count ?? 0}\n";
                if (_parameters != null)
                {
                    foreach (var p in _parameters.Take(5))
                    {
                        debugInfo += $"    {p.Key} = {p.Value}\n";
                    }
                }
                System.IO.File.AppendAllText(debugPath, debugInfo + "\n");
            }
            catch { }

            // Skip internal parameters starting with __
            var realParams = _parameters?.Where(p => !p.Key.StartsWith("__")).ToList();

            // If no real parameters yet, show loading
            if (realParams == null || !realParams.Any())
            {
                // For file-based operations that set _filePath directly
                if (!string.IsNullOrEmpty(_filePath))
                    return GetShortPath(_filePath);

                // Otherwise indicate we're waiting for params
                return "loading...";
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
