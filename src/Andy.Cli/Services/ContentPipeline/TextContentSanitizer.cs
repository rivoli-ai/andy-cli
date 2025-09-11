using System;
using System.Text.RegularExpressions;
using System.Collections.Generic; // Added for List

namespace Andy.Cli.Services.ContentPipeline;

/// <summary>
/// Sanitizes content blocks by removing excessive whitespace, empty JSON blocks, and formatting issues
/// </summary>
public class TextContentSanitizer : IContentSanitizer
{
    // Redact internal tool disclosures
    private static readonly Regex ToolJsonPattern = new(@"\{[^}]*\""(?:tool|function|tool_call)\""[^}]*\}", RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ToolMentionPattern = new(@"(?i)\b(tool call|using (?:a|the) tool|call(?:ing)? a tool|invoke|invoking|function call|list_directory|read_file|bash_command|create_directory|codeindex)\b", RegexOptions.Compiled);
    private static readonly Regex EmptyJsonPattern = new(@"`json\s*\n\s*`", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex EmptyTripleJsonPattern = new(@"```json\s*\n\s*```", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ConsecutiveNewlinesPattern = new(@"\n\s*\n\s*\n+", RegexOptions.Compiled);  // 3+ newlines with optional whitespace
    private static readonly Regex DoubleNewlinesPattern = new(@"\n\s*\n", RegexOptions.Compiled);  // 2 newlines with optional whitespace
    private static readonly Regex TrailingWhitespacePattern = new(@"[ \t]+$", RegexOptions.Multiline | RegexOptions.Compiled);

    public IContentBlock Sanitize(IContentBlock block)
    {
        return block switch
        {
            TextBlock textBlock => SanitizeTextBlock(textBlock),
            CodeBlock codeBlock => SanitizeCodeBlock(codeBlock),
            SystemMessageBlock systemBlock => SanitizeSystemMessageBlock(systemBlock),
            _ => block
        };
    }

    private TextBlock SanitizeTextBlock(TextBlock block)
    {
        var content = block.Content;
        // Redact any internal tool disclosure before other processing
        content = RedactToolMentions(content);
        
        // Remove empty JSON blocks
        content = EmptyJsonPattern.Replace(content, "");
        content = EmptyTripleJsonPattern.Replace(content, "");
        
        // Remove trailing whitespace from lines
        content = TrailingWhitespacePattern.Replace(content, "");
        
        // First, remove 3+ consecutive newlines (with optional whitespace between)
        content = ConsecutiveNewlinesPattern.Replace(content, "\n");
        
        // Then, remove ALL double newlines to create single-spaced output
        content = DoubleNewlinesPattern.Replace(content, "\n");
        
        // Trim overall content
        content = content.Trim();
        
        // If content is now empty or whitespace, mark as incomplete
        if (string.IsNullOrWhiteSpace(content))
        {
            return new TextBlock(block.Id, "", block.Priority)
            {
                IsComplete = false
            };
        }
        
        return new TextBlock(block.Id, content, block.Priority)
        {
            IsComplete = block.IsComplete
        };
    }
    
    private static string RedactToolMentions(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        // Remove inline tool JSON objects
        var redacted = ToolJsonPattern.Replace(content, "");
        // Drop any line that explicitly mentions tools, except clear error notifications
        var lines = redacted.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            // Preserve explicit error lines for tool failures
            if (trimmed.StartsWith("**Tool execution error:**", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Tool execution failed:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Error executing tool:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Tool '", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(line);
                continue;
            }
            if (!ToolMentionPattern.IsMatch(line)) kept.Add(line);
        }
        return string.Join("\n", kept);
    }
    
    private CodeBlock SanitizeCodeBlock(CodeBlock block)
    {
        var code = block.Code;
        
        // Remove trailing whitespace from lines
        code = TrailingWhitespacePattern.Replace(code, "");
        
        // Trim overall code but preserve internal structure
        code = code.Trim();
        
        // If code is now empty or whitespace, mark as incomplete
        if (string.IsNullOrWhiteSpace(code))
        {
            return new CodeBlock(block.Id, "", block.Language, block.Priority)
            {
                IsComplete = false
            };
        }
        
        return new CodeBlock(block.Id, code, block.Language, block.Priority)
        {
            IsComplete = block.IsComplete
        };
    }
    
    private SystemMessageBlock SanitizeSystemMessageBlock(SystemMessageBlock block)
    {
        var message = block.Message;
        
        // Basic sanitization for system messages
        message = TrailingWhitespacePattern.Replace(message, "");
        message = message.Trim();
        
        if (string.IsNullOrWhiteSpace(message))
        {
            return new SystemMessageBlock(block.Id, "", block.Type, block.Priority)
            {
                IsComplete = false
            };
        }
        
        return new SystemMessageBlock(block.Id, message, block.Type, block.Priority)
        {
            IsComplete = block.IsComplete
        };
    }
}