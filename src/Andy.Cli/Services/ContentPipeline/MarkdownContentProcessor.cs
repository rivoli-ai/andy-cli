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
                    // Special handling for markdown code blocks - process them as markdown
                    if (part.Language != null && part.Language.Equals("markdown", StringComparison.OrdinalIgnoreCase))
                    {
                        // Recursively process the markdown content
                        var nestedBlocks = Process(part.Code, $"{blockIdPrefix}nested_");
                        foreach (var nestedBlock in nestedBlocks)
                        {
                            blocks.Add(nestedBlock);
                        }
                    }
                    else
                    {
                        // Regular code block
                        blocks.Add(new CodeBlock(blockId, part.Code, part.Language));
                    }
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
        var index = 0;
        var length = content.Length;

        while (index < length)
        {
            // Look for opening ```
            var codeBlockStart = content.IndexOf("```", index, StringComparison.Ordinal);

            if (codeBlockStart == -1)
            {
                // No more code blocks, add remaining as text
                var remaining = content.Substring(index);
                if (!string.IsNullOrWhiteSpace(remaining))
                {
                    parts.Add(new ContentPart { Content = remaining.Trim(), IsCodeBlock = false });
                }
                break;
            }

            // Add text before code block
            if (codeBlockStart > index)
            {
                var textBefore = content.Substring(index, codeBlockStart - index);
                if (!string.IsNullOrWhiteSpace(textBefore))
                {
                    parts.Add(new ContentPart { Content = textBefore.Trim(), IsCodeBlock = false });
                }
            }

            // Extract language (everything after ``` until newline)
            var languageStart = codeBlockStart + 3;
            var languageEnd = languageStart;
            while (languageEnd < length && content[languageEnd] != '\r' && content[languageEnd] != '\n')
            {
                languageEnd++;
            }

            var language = content.Substring(languageStart, languageEnd - languageStart).Trim();

            // Skip past the newline(s) after language
            var codeStart = languageEnd;
            while (codeStart < length && (content[codeStart] == '\r' || content[codeStart] == '\n'))
            {
                codeStart++;
            }

            // Find matching closing ``` by counting nesting levels
            var nestLevel = 1;
            var searchIndex = codeStart;
            var codeEnd = -1;

            while (searchIndex < length && nestLevel > 0)
            {
                var nextTripleBacktick = content.IndexOf("```", searchIndex, StringComparison.Ordinal);

                if (nextTripleBacktick == -1)
                {
                    // No closing found, treat rest as code
                    codeEnd = length;
                    break;
                }

                // Check if this is at start of line or after newline (proper code block delimiter)
                var isProperDelimiter = nextTripleBacktick == 0 ||
                                       content[nextTripleBacktick - 1] == '\n' ||
                                       content[nextTripleBacktick - 1] == '\r';

                if (isProperDelimiter)
                {
                    // Check what follows the ``` - if it's a language identifier, it's opening
                    var afterBackticks = nextTripleBacktick + 3;
                    var hasLanguageOrNewline = afterBackticks >= length ||
                                               content[afterBackticks] == '\r' ||
                                               content[afterBackticks] == '\n' ||
                                               char.IsLetterOrDigit(content[afterBackticks]);

                    if (hasLanguageOrNewline)
                    {
                        // Check if it's a closing (followed by newline or end)
                        var isClosing = afterBackticks >= length ||
                                       content[afterBackticks] == '\r' ||
                                       content[afterBackticks] == '\n';

                        if (isClosing)
                        {
                            nestLevel--;
                            if (nestLevel == 0)
                            {
                                codeEnd = nextTripleBacktick;
                                break;
                            }
                        }
                        else
                        {
                            // Opening a nested block
                            nestLevel++;
                        }
                    }
                }

                searchIndex = nextTripleBacktick + 3;
            }

            if (codeEnd == -1)
            {
                codeEnd = length;
            }

            // Extract code content (trim trailing newlines)
            var code = content.Substring(codeStart, codeEnd - codeStart);
            code = code.TrimEnd('\r', '\n');

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

            // Move past the closing ```
            index = codeEnd + 3;

            // Skip trailing newlines after closing ```
            while (index < length && (content[index] == '\r' || content[index] == '\n'))
            {
                index++;
            }
        }

        // If no parts found, add entire content as text
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