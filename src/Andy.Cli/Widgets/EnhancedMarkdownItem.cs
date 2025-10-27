using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.TextWrapping;
using Andy.Tui.DisplayList;
using Andy.Tui.Layout;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets;

/// <summary>
/// Enhanced markdown feed item that uses proper text wrapping with word boundaries and hyphenation.
/// </summary>
public sealed class EnhancedMarkdownItem : IFeedItem
{
    private readonly string _md;
    private readonly string _originalMd;
    private readonly ITextWrapper _textWrapper;
    private readonly TextWrappingOptions _wrappingOptions;

    public EnhancedMarkdownItem(string markdown, ITextWrapper textWrapper, TextWrappingOptions? wrappingOptions = null)
    {
        _textWrapper = textWrapper ?? throw new ArgumentNullException(nameof(textWrapper));
        _wrappingOptions = wrappingOptions ?? new TextWrappingOptions();
        
        _originalMd = (markdown ?? string.Empty).TrimEnd();
        
        // Preprocess markdown to prevent "You" from being highlighted
        _md = _originalMd;
        _md = System.Text.RegularExpressions.Regex.Replace(_md, @"\bYou\b(?!:)", "Y\u200Cou");
    }

    public int MeasureLineCount(int width)
    {
        if (width <= 0) return 1;

        // Apply paragraph spacing transformation
        var markdown = SimulateParagraphSpacing(_md);
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int totalLines = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                totalLines++;
            }
            else
            {
                // Use the text wrapper to get accurate line count
                int effectiveWidth = Math.Max(1, width); // Use full width
                totalLines += _textWrapper.MeasureLineCount(line, effectiveWidth, _wrappingOptions);
            }
        }

        return Math.Max(1, totalLines);
    }

    public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
    {
        if (width <= 0 || maxLines <= 0) return;

        // Pre-process text with proper wrapping
        var lines = _md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var wrappedLines = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                wrappedLines.Add("");
            }
            else
            {
                // Use text wrapper to properly wrap the line
                int effectiveWidth = Math.Max(1, width); // Use full width
                var wrapped = _textWrapper.WrapText(line, effectiveWidth, _wrappingOptions);
                wrappedLines.AddRange(wrapped.Lines);
            }
        }

        // Guard against invalid startLine
        if (startLine >= wrappedLines.Count || startLine < 0) return;

        int end = Math.Min(wrappedLines.Count, startLine + maxLines);
        var slice = string.Join("\n", wrappedLines.Skip(startLine).Take(maxLines));

        // Detect simple HTML links and render with Link widget
        if (TryRenderSimpleHtmlLink(slice, x, y, width, maxLines, baseDl, b)) return;

        // Use Andy.Tui MarkdownRenderer for the pre-wrapped text
        var r = new Andy.Tui.Widgets.MarkdownRenderer();
        r.SetText(slice);
        r.Render(new L.Rect(x, y, width, maxLines), baseDl, b);
    }

    /// <summary>
    /// Simulates the paragraph spacing that Andy.Tui.Widgets.MarkdownRenderer will apply.
    /// </summary>
    private static string SimulateParagraphSpacing(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var result = new List<string>();

        for (int i = 0; i < lines.Count; i++)
        {
            string current = lines[i];
            string next = i < lines.Count - 1 ? lines[i + 1] : "";

            result.Add(current);

            // Add spacing between paragraphs
            if (!string.IsNullOrWhiteSpace(current) && 
                !string.IsNullOrWhiteSpace(next) && 
                GetLineType(current) == LineType.Text && 
                GetLineType(next) == LineType.Text)
            {
                result.Add(""); // Add blank line between paragraphs
            }
        }

        return string.Join("\n", result);
    }

    private static LineType GetLineType(string line)
    {
        var trimmed = line.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
            return LineType.Empty;

        // Check for headings
        if (trimmed.StartsWith("# "))
            return LineType.Heading;
        if (trimmed.StartsWith("## "))
            return LineType.Heading;
        if (trimmed.StartsWith("### "))
            return LineType.Heading;

        // Check for list items
        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") ||
            trimmed.StartsWith("• ") || trimmed.StartsWith("★ ") ||
            System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+\.\s"))
            return LineType.List;

        return LineType.Text;
    }

    private enum LineType
    {
        Empty,
        Text,
        Heading,
        List
    }

    private static bool TryRenderSimpleHtmlLink(string text, int x, int y, int width, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
    {
        // Simple HTML link detection and rendering
        var linkMatch = System.Text.RegularExpressions.Regex.Match(text, @"<a\s+href=""([^""]+)""[^>]*>([^<]+)</a>");
        if (linkMatch.Success)
        {
            var url = linkMatch.Groups[1].Value;
            var linkText = linkMatch.Groups[2].Value;
            
            // Render as a simple link widget
            var linkWidget = new Andy.Tui.Widgets.Link();
            linkWidget.SetText(linkText);
            linkWidget.SetUrl(url);
            linkWidget.Render(new L.Rect(x, y, width, maxLines), baseDl, b);
            return true;
        }

        return false;
    }
}
