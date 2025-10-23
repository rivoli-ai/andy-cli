using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services.ContentPipeline;

/// <summary>
/// Processes raw markdown content into structured content blocks
/// </summary>
public class MarkdownContentProcessor : IContentProcessor
{
    private static readonly Regex CodeBlockPattern = new(@"```(\w*)\s*[\r\n]+(.*?)[\r\n]+```", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InlineCodePattern = new(@"`([^`\n]+)`", RegexOptions.Compiled);

    public IEnumerable<IContentBlock> Process(string rawContent, string blockIdPrefix = "")
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return Enumerable.Empty<IContentBlock>();

        var blocks = new List<IContentBlock>();
        var blockCounter = 0;
        
        // Split content by code blocks first
        var parts = SplitByCodeBlocks(rawContent);
        
        foreach (var part in parts)
        {
            var blockId = $"{blockIdPrefix}block_{blockCounter++}";
            
            if (part.IsCodeBlock)
            {
                // Only add code blocks with meaningful content
                if (!string.IsNullOrWhiteSpace(part.Code))
                {
                    blocks.Add(new CodeBlock(blockId, part.Code, part.Language));
                }
            }
            else
            {
                // Process text content
                var textContent = part.Content;
                
                // Only add text blocks with meaningful content
                if (!string.IsNullOrWhiteSpace(textContent))
                {
                    blocks.Add(new TextBlock(blockId, textContent));
                }
            }
        }

        return blocks;
    }

    private List<ContentPart> SplitByCodeBlocks(string content)
    {
        var parts = new List<ContentPart>();
        var lastIndex = 0;
        
        var matches = CodeBlockPattern.Matches(content);
        
        foreach (Match match in matches)
        {
            // Add text before code block
            if (match.Index > lastIndex)
            {
                var textContent = content.Substring(lastIndex, match.Index - lastIndex);
                if (!string.IsNullOrWhiteSpace(textContent))
                {
                    parts.Add(new ContentPart { Content = textContent.Trim(), IsCodeBlock = false });
                }
            }
            
            // Add code block
            var language = match.Groups[1].Value;
            var code = match.Groups[2].Value;
            
            if (!string.IsNullOrWhiteSpace(code))
            {
                parts.Add(new ContentPart 
                { 
                    Content = code, 
                    Code = code,
                    Language = string.IsNullOrEmpty(language) ? null : language,
                    IsCodeBlock = true 
                });
            }
            
            lastIndex = match.Index + match.Length;
        }
        
        // Add remaining text
        if (lastIndex < content.Length)
        {
            var remainingContent = content.Substring(lastIndex);
            if (!string.IsNullOrWhiteSpace(remainingContent))
            {
                parts.Add(new ContentPart { Content = remainingContent.Trim(), IsCodeBlock = false });
            }
        }
        
        // If no code blocks found, add the entire content as text
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(content))
        {
            parts.Add(new ContentPart { Content = content.Trim(), IsCodeBlock = false });
        }
        
        return parts;
    }
    
    private class ContentPart
    {
        public string Content { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? Language { get; set; }
        public bool IsCodeBlock { get; set; }
    }
}