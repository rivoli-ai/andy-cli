using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.TextWrapping;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets;

/// <summary>
/// Enhanced FeedView that uses proper text wrapping with word boundaries and hyphenation.
/// </summary>
public sealed class EnhancedFeedView
{
    private readonly List<IFeedItem> _items = new();
    private readonly object _itemsLock = new();
    private readonly ITextWrapper _textWrapper;
    private readonly TextWrappingOptions _wrappingOptions;
    
    private int _scrollOffset;
    private bool _followTail = true;
    private bool _focused;
    private int _animRemaining;
    private int _animSpeed = 2;

    public EnhancedFeedView(ITextWrapper textWrapper, TextWrappingOptions? wrappingOptions = null)
    {
        _textWrapper = textWrapper ?? throw new ArgumentNullException(nameof(textWrapper));
        _wrappingOptions = wrappingOptions ?? new TextWrappingOptions();
    }

    /// <summary>When true and scrolled to bottom, keep content pinned to bottom.</summary>
    public bool FollowTail { get => _followTail; set => _followTail = value; }
    
    /// <summary>Set focus state for the feed to affect rendering.</summary>
    public void SetFocused(bool focused) { _focused = focused; }
    
    /// <summary>Set animation speed in lines per frame.</summary>
    public void SetAnimationSpeed(int linesPerFrame) { _animSpeed = Math.Max(0, linesPerFrame); }
    
    /// <summary>Append a new item to the feed.</summary>
    public void AddItem(IFeedItem item)
    {
        lock (_itemsLock)
        {
            _items.Add(item);
            _animRemaining += item.MeasureLineCount(80); // Estimate for animation
        }
    }

    /// <summary>Append markdown content with enhanced text wrapping.</summary>
    public void AddMarkdownRich(string md)
    {
        // Split markdown by tables and render each part appropriately
        var parts = SplitMarkdownWithTables(md);
        foreach (var part in parts)
        {
            if (part.IsTable)
            {
                AddItem(new TableItem(part.Headers!.ToList(), part.Rows!.ToList(), part.Title));
            }
            else if (!string.IsNullOrWhiteSpace(part.Content))
            {
                // Use enhanced markdown item with proper text wrapping
                AddItem(new EnhancedMarkdownItem(part.Content, _textWrapper, _wrappingOptions));
            }
        }
    }

    /// <summary>Convenience: append code block item.</summary>
    public void AddCode(string code, string? language = null) => AddItem(new CodeBlockItem(code, language));

    /// <summary>Append a user message bubble with enhanced text wrapping.</summary>
    public void AddUserMessage(string text, int messageNumber = 0)
    {
        var bubble = new UserMessageBubble(text, messageNumber, _textWrapper, _wrappingOptions);
        AddItem(bubble);
    }

    /// <summary>Clear all items from the feed.</summary>
    public void Clear()
    {
        lock (_itemsLock)
        {
            _items.Clear();
            _scrollOffset = 0;
            _animRemaining = 0;
        }
    }

    /// <summary>Add simple markdown content.</summary>
    public void AddMarkdown(string md) => AddItem(new MarkdownItem(md));

    /// <summary>Add response separator.</summary>
    public void AddResponseSeparator(int inputTokens = 0, int outputTokens = 0, string pattern = "━━ ◆ ━━") => AddItem(new ResponseSeparatorItem(inputTokens, outputTokens, pattern));

    /// <summary>Add streaming message item.</summary>
    public StreamingMessageItem AddStreamingMessage()
    {
        var item = new StreamingMessageItem();
        AddItem(item);
        return item;
    }

    /// <summary>Add tool execution item.</summary>
    public void AddToolExecution(string toolId, Dictionary<string, object?> parameters, string? result = null, bool isSuccess = true)
    {
        var item = new ToolExecutionItem(toolId, parameters, result, isSuccess);
        AddItem(item);
    }

    /// <summary>Add tool execution start item.</summary>
    public void AddToolExecutionStart(string toolId, string toolName, Dictionary<string, object?>? parameters = null)
    {
        var item = new RunningToolItem(toolId, toolName);
        if (parameters != null)
        {
            item.SetParameters(parameters);
        }
        AddItem(item);
    }

    /// <summary>Add tool execution detail item.</summary>
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
                    break; // Update only the first matching tool
                }
            }
        }
    }

    /// <summary>Add tool execution complete item.</summary>
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
                    break; // Update only the first matching tool
                }
            }
        }
    }

    /// <summary>Add processing indicator.</summary>
    public void AddProcessingIndicator()
    {
        var item = new ProcessingIndicatorItem();
        AddItem(item);
    }

    /// <summary>Clear processing indicator.</summary>
    public void ClearProcessingIndicator()
    {
        lock (_itemsLock)
        {
            _items.RemoveAll(item => item is ProcessingIndicatorItem);
        }
    }

    /// <summary>Update animation frame.</summary>
    public void Tick()
    {
        // Animation is handled in Render method
    }

    /// <summary>Update running tool parameters.</summary>
    public void UpdateRunningToolParameters(string toolName, Dictionary<string, object?> parameters)
    {
        lock (_itemsLock)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i] is RunningToolItem runningTool && runningTool.ToolName == toolName && !runningTool.IsComplete)
                {
                    runningTool.SetParameters(parameters);
                    break;
                }
            }
        }
    }

    /// <summary>Update tool by exact ID.</summary>
    public void UpdateToolByExactId(string exactToolId, Dictionary<string, object?> parameters)
    {
        lock (_itemsLock)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i] is RunningToolItem runningTool && runningTool.ToolId == exactToolId)
                {
                    runningTool.SetParameters(parameters);
                    break;
                }
            }
        }
    }

    /// <summary>Force update all matching tools.</summary>
    public void ForceUpdateAllMatchingTools(string toolId, string toolName, Dictionary<string, object?> parameters)
    {
        lock (_itemsLock)
        {
            foreach (var item in _items)
            {
                if (item is RunningToolItem runningTool && runningTool.ToolId == toolId)
                {
                    runningTool.SetParameters(parameters);
                }
            }
        }
    }

    /// <summary>Update tool result.</summary>
    public void UpdateToolResult(string toolId, string toolName, bool success, object? resultData, Dictionary<string, object?>? parameters)
    {
        lock (_itemsLock)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i] is RunningToolItem runningTool && runningTool.ToolId == toolId)
                {
                    runningTool.SetComplete(success, "0ms");
                    if (resultData != null)
                    {
                        runningTool.SetResult(resultData.ToString() ?? "");
                    }
                    break;
                }
            }
        }
    }

    /// <summary>Scroll the feed by the specified number of lines.</summary>
    public int ScrollLines(int deltaLines, int viewportHeight)
    {
        lock (_itemsLock)
        {
            var totalLines = GetTotalLineCount(viewportHeight);
            var maxScroll = Math.Max(0, totalLines - viewportHeight);
            
            _scrollOffset = Math.Clamp(_scrollOffset - deltaLines, 0, maxScroll);
            
            if (_scrollOffset == 0)
                _followTail = true;
            else
                _followTail = false;
                
            return _scrollOffset;
        }
    }

    /// <summary>Render the feed within the specified rectangle.</summary>
    public void Render(in L.Rect rect, DL.DisplayList baseDl, DL.DisplayListBuilder b)
    {
        lock (_itemsLock)
        {
            var (x, y, w, h) = (rect.X, rect.Y, rect.Width, rect.Height);
            
            // Calculate scroll position
            var totalLines = GetTotalLineCount((int)w);
            var startLine = _followTail ? Math.Max(0, totalLines - (int)h) : _scrollOffset;
            
            // Render items
            RenderItems((int)x, (int)y, (int)w, (int)h, startLine, baseDl, b);
            
            // Update animation
            if (_animRemaining > 0)
            {
                _animRemaining = Math.Max(0, _animRemaining - _animSpeed);
            }
        }
    }

    private int GetTotalLineCount(int width)
    {
        if (width <= 0) return 1;
        
        int totalLines = 0;
        foreach (var item in _items)
        {
            totalLines += item.MeasureLineCount(width);
        }
        return Math.Max(1, totalLines);
    }

    private void RenderItems(int x, int y, int w, int h, int startLine, DL.DisplayList baseDl, DL.DisplayListBuilder b)
    {
        int currentLine = 0;
        int renderedLines = 0;
        
        foreach (var item in _items)
        {
            var itemLines = item.MeasureLineCount(w);
            
            if (currentLine + itemLines > startLine && renderedLines < h)
            {
                var itemStartLine = Math.Max(0, startLine - currentLine);
                var maxLines = Math.Min(h - renderedLines, itemLines - itemStartLine);
                
                if (maxLines > 0)
                {
                    item.RenderSlice(x, y + renderedLines, w, itemStartLine, maxLines, baseDl, b);
                    renderedLines += maxLines;
                }
            }
            
            currentLine += itemLines;
            
            if (renderedLines >= h) break;
        }
    }

    private static List<MarkdownPart> SplitMarkdownWithTables(string markdown)
    {
        // Simplified table splitting - in a real implementation, this would be more sophisticated
        var parts = new List<MarkdownPart>();
        
        if (string.IsNullOrWhiteSpace(markdown))
        {
            parts.Add(new MarkdownPart { Content = markdown, IsTable = false });
            return parts;
        }
        
        // For now, just treat everything as regular markdown content
        parts.Add(new MarkdownPart { Content = markdown, IsTable = false });
        return parts;
    }

    private class MarkdownPart
    {
        public string Content { get; set; } = string.Empty;
        public bool IsTable { get; set; }
        public string[]? Headers { get; set; }
        public string[][]? Rows { get; set; }
        public string? Title { get; set; }
    }
}

/// <summary>
/// Enhanced user message bubble with proper text wrapping.
/// </summary>
public sealed class UserMessageBubble : IFeedItem
{
    private readonly string _text;
    private readonly int _messageNumber;
    private readonly ITextWrapper _textWrapper;
    private readonly TextWrappingOptions _wrappingOptions;

    public UserMessageBubble(string text, int messageNumber, ITextWrapper textWrapper, TextWrappingOptions wrappingOptions)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _messageNumber = messageNumber;
        _textWrapper = textWrapper ?? throw new ArgumentNullException(nameof(textWrapper));
        _wrappingOptions = wrappingOptions ?? throw new ArgumentNullException(nameof(wrappingOptions));
    }

    public int MeasureLineCount(int width)
    {
        if (width <= 0) return 1;
        
        // Account for bubble margins and padding (consistent with RenderSlice)
        int effectiveWidth = Math.Max(1, width - 4); // 2 chars padding on each side
        return _textWrapper.MeasureLineCount(_text, effectiveWidth, _wrappingOptions);
    }

    public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
    {
        var theme = Themes.Theme.Current;
        
        // Render bubble background
        b.DrawRect(new DL.Rect(x, y, width, maxLines, theme.DialogBackground));
        b.DrawBorder(new DL.Border(x, y, width, maxLines, "single", theme.Border));
        
        // Render message number if provided
        if (_messageNumber > 0)
        {
            var numberText = _messageNumber.ToString();
            b.DrawText(new DL.TextRun(x + 1, y, numberText, theme.Primary, theme.DialogBackground, DL.CellAttrFlags.Bold));
        }
        
        // Render wrapped text
        int effectiveWidth = Math.Max(1, width - 4); // Account for padding
        var wrapped = _textWrapper.WrapText(_text, effectiveWidth, _wrappingOptions);
        
        int textY = y + (_messageNumber > 0 ? 1 : 0);
        int textX = x + 2;
        
        for (int i = 0; i < Math.Min(maxLines, wrapped.Lines.Count); i++)
        {
            var line = wrapped.Lines[i];
            b.DrawText(new DL.TextRun(textX, textY + i, line, theme.Text, theme.DialogBackground, DL.CellAttrFlags.None));
        }
    }
}
