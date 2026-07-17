using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services.ContentPipeline;

/// <summary>
/// Sanitizes content blocks by removing confirmed protocol/tool-call artifacts and
/// normalizing whitespace, while preserving legitimate prose and Markdown structure.
///
/// Filtering is STRUCTURAL: it removes only artifacts that are unambiguously machine
/// protocol - tool-call XML tags, fenced tool-call blocks, and JSON tool-call envelopes -
/// never lines that merely contain a phrase like "tool call" or a tool name in prose.
/// Paragraph spacing, code fences, lists and tables are preserved.
/// </summary>
public class TextContentSanitizer : IContentSanitizer
{
    // <tool_call>...</tool_call> and close variants (Qwen and similar emit these).
    private static readonly Regex ToolCallTagPattern = new(
        @"<\s*(tool_call|tool_code|tool_use|function_call)\s*>.*?<\s*/\s*\1\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fenced blocks whose info string marks them as a tool-call protocol block, e.g.
    // ```tool_call\n{...}\n```  -- removed whole. Ordinary ```json / ```csharp prose is kept.
    private static readonly Regex ToolCallFencePattern = new(
        @"```[ \t]*(tool_call|tool_code|tool_use|function_call)[ \t]*\r?\n.*?\r?\n?```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Empty JSON fences left behind by upstream extraction.
    private static readonly Regex EmptyJsonPattern = new(@"`json\s*\n\s*`", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex EmptyTripleJsonPattern = new(@"```json\s*\n\s*```", RegexOptions.Multiline | RegexOptions.Compiled);

    // 3+ consecutive newlines (i.e. 2+ blank lines, with optional intervening whitespace)
    // collapse to a single blank line. This preserves paragraph spacing while trimming
    // runaway gaps. A single blank line (2 newlines) is left untouched.
    private static readonly Regex ExcessBlankLinesPattern = new(@"\n(?:[ \t]*\n){2,}", RegexOptions.Compiled);
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

        // Normalize line endings first - convert all \r\n and \r to \n
        content = content?.Replace("\r\n", "\n").Replace("\r", "\n") ?? "";

        // Remove confirmed protocol/tool-call artifacts (structural, not phrase-based).
        content = RemoveToolCallArtifacts(content);

        // Remove empty JSON blocks
        content = EmptyJsonPattern.Replace(content, "");
        content = EmptyTripleJsonPattern.Replace(content, "");

        // Remove trailing whitespace from lines
        content = TrailingWhitespacePattern.Replace(content, "");

        // Collapse 3+ consecutive newlines to a single blank line. Do NOT collapse a single
        // blank line between paragraphs - that spacing is legitimate Markdown.
        content = ExcessBlankLinesPattern.Replace(content, "\n\n");

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

    /// <summary>
    /// Removes only confirmed protocol/tool-call artifacts: tool-call XML tags, fenced
    /// tool-call blocks, and standalone JSON tool-call envelopes. Prose that merely mentions
    /// a tool name or the words "tool call" is left completely intact.
    /// </summary>
    private static string RemoveToolCallArtifacts(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        content = ToolCallTagPattern.Replace(content, "");
        content = ToolCallFencePattern.Replace(content, "");
        content = StripJsonToolCallEnvelopes(content);

        return content;
    }

    /// <summary>
    /// Scans for balanced top-level JSON objects and removes only those that are structurally
    /// tool-call envelopes: an object carrying a "tool_call"/"tool_calls" key, or both a
    /// name-like key ("name"/"tool"/"function") and an argument-like key
    /// ("arguments"/"parameters"/"args"). Objects that do not match are left untouched, so
    /// ordinary JSON examples and prose survive.
    /// </summary>
    private static string StripJsonToolCallEnvelopes(string content)
    {
        if (content.IndexOf('{') < 0) return content;

        var result = new StringBuilder(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c != '{')
            {
                result.Append(c);
                i++;
                continue;
            }

            var end = FindMatchingBrace(content, i);
            if (end < 0)
            {
                // Unbalanced - copy the rest verbatim and stop scanning.
                result.Append(content, i, content.Length - i);
                break;
            }

            var candidate = content.Substring(i, end - i + 1);
            if (LooksLikeToolCallEnvelope(candidate))
            {
                // Skip (remove) the envelope entirely.
                i = end + 1;
            }
            else
            {
                result.Append(candidate);
                i = end + 1;
            }
        }

        return result.ToString();
    }

    private static int FindMatchingBrace(string s, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    private static bool LooksLikeToolCallEnvelope(string json)
    {
        // Only the top-level keys of the object matter. Match keys followed by a colon so we
        // do not trip on the same word appearing as a value or inside prose.
        bool HasKey(string key) =>
            Regex.IsMatch(json, "\"" + Regex.Escape(key) + "\"\\s*:", RegexOptions.IgnoreCase);

        if (HasKey("tool_call") || HasKey("tool_calls"))
        {
            return true;
        }

        var hasName = HasKey("name") || HasKey("tool") || HasKey("function");
        var hasArgs = HasKey("arguments") || HasKey("parameters") || HasKey("args");
        return hasName && hasArgs;
    }

    private CodeBlock SanitizeCodeBlock(CodeBlock block)
    {
        // A fenced block whose language marks it as tool-call protocol (e.g. ```tool_call)
        // is an artifact, not user-facing code - drop it structurally.
        if (IsToolCallLanguage(block.Language))
        {
            return new CodeBlock(block.Id, "", block.Language, block.Priority)
            {
                IsComplete = false
            };
        }

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

    private static bool IsToolCallLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var lang = language.Trim();
        return lang.Equals("tool_call", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("tool_code", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("tool_use", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("function_call", StringComparison.OrdinalIgnoreCase);
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
