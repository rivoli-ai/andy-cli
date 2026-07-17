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
    // Opening tool-call markers (Qwen and similar emit these): <tool_call>, <tool_use>, etc.
    private static readonly Regex ToolCallOpenTag = new(
        @"<\s*(tool_call|tool_code|tool_use|function_call)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Closing tool-call markers: </tool_call>, </tool_use>, etc.
    private static readonly Regex ToolCallCloseTag = new(
        @"<\s*/\s*(tool_call|tool_code|tool_use|function_call)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Cross-block streaming state: when a stream splits a <tool_call> ... </tool_call> across
    // blocks, the opening marker arrives in one block and the close in a later one. This holds
    // the name of an open (not-yet-closed) tool-call tag so its body is suppressed until the
    // matching close arrives, instead of leaking a raw protocol fragment to the user. Only ever
    // touched by the single pipeline consumer thread, and the sanitizer is created per request.
    private string? _openToolCallTag;

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
    /// Removes only confirmed protocol/tool-call artifacts: tool-call XML tags (including ones
    /// split across streamed blocks), fenced tool-call blocks, and standalone JSON tool-call
    /// envelopes. Prose that merely mentions a tool name or the words "tool call" is left
    /// completely intact.
    /// </summary>
    private string RemoveToolCallArtifacts(string content)
    {
        // Tag handling runs even for empty content so a mid-tool-call streaming state can be
        // observed/advanced (an empty chunk inside an open tag stays suppressed).
        content = RemoveToolCallTags(content ?? "");

        if (string.IsNullOrEmpty(content)) return content;

        content = ToolCallFencePattern.Replace(content, "");
        content = StripJsonToolCallEnvelopes(content);

        return content;
    }

    /// <summary>
    /// Removes tool-call XML tags and their bodies, handling both complete tags within a single
    /// block and tags split across streamed blocks. When an opening marker has no matching close
    /// in the same block, the remainder is suppressed and <see cref="_openToolCallTag"/> is set
    /// so the continuation (up to the eventual close) is suppressed in later blocks too. This
    /// guarantees a real streamed tool call never renders literally, even mid-split.
    /// </summary>
    private string RemoveToolCallTags(string content)
    {
        if (content.Length == 0)
        {
            // Nothing to scan; preserve any open-tag streaming state as-is.
            return content;
        }

        var sb = new StringBuilder(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            if (_openToolCallTag != null)
            {
                // Inside an open tool call from a previous block: drop everything up to and
                // including the matching close, if it appears in this block.
                var close = FindCloseTag(content, i, _openToolCallTag);
                if (close != null)
                {
                    _openToolCallTag = null;
                    i = close.Index + close.Length;
                    continue;
                }

                // Still no close: the rest of this block belongs to the tool call. Drop it.
                break;
            }

            var open = ToolCallOpenTag.Match(content, i);
            if (!open.Success)
            {
                sb.Append(content, i, content.Length - i);
                break;
            }

            // Keep the prose that precedes the opening marker.
            sb.Append(content, i, open.Index - i);

            var name = open.Groups[1].Value;
            var closeMatch = FindCloseTag(content, open.Index + open.Length, name);
            if (closeMatch != null)
            {
                // Complete tag within this block: drop the whole span.
                i = closeMatch.Index + closeMatch.Length;
            }
            else
            {
                // Streaming split: opening marker with no close here. Drop the remainder and
                // remember we are inside a tool call so later blocks stay suppressed.
                _openToolCallTag = name;
                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Finds the first closing tool-call marker at or after <paramref name="start"/> whose tag
    /// name matches <paramref name="name"/> (case-insensitive). Returns null if none is present.
    /// </summary>
    private static Match? FindCloseTag(string content, int start, string name)
    {
        if (start > content.Length) return null;
        var m = ToolCallCloseTag.Match(content, start);
        while (m.Success)
        {
            if (string.Equals(m.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
            m = m.NextMatch();
        }
        return null;
    }

    /// <summary>
    /// Scans for balanced JSON objects and removes only those that are tool-call protocol
    /// envelopes. Removed wherever they appear: an explicit "tool_call"/"tool_calls" container,
    /// and the concrete invocation shape (a name-like key plus literal "arguments"/"args").
    /// Removed only when standalone on their own line: the ambiguous name+"parameters" shape
    /// (which is also a function-schema definition a user may discuss) and bare "tool"/"function"
    /// markers. Everything else - including inline JSON in a sentence - is preserved.
    ///
    /// Scanning is robust to stray/unbalanced braces in prose: an unbalanced '{' is emitted
    /// literally and scanning continues, so it neither eats the surrounding prose nor prevents a
    /// genuine later envelope from being stripped.
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
                // Unbalanced stray '{' (typically in prose): emit it literally and keep scanning
                // so a genuine envelope later in the text is still detected and stripped.
                result.Append(c);
                i++;
                continue;
            }

            var candidate = content.Substring(i, end - i + 1);
            var kind = ClassifyEnvelope(candidate);
            var strip = kind == EnvelopeKind.Definitive
                || (kind == EnvelopeKind.Marker && IsStandaloneSpan(content, i, end));

            if (!strip)
            {
                result.Append(candidate);
                i = end + 1;
                continue;
            }

            RemoveEnvelopeSpan(result, content, ref i, end);
        }

        return result.ToString();
    }

    /// <summary>
    /// Removes the envelope occupying content[start..end] and collapses the whitespace it leaves
    /// behind: an envelope alone on its line takes the whole line (no blank line remains); an
    /// inline envelope between words collapses to a single separating space.
    /// </summary>
    private static void RemoveEnvelopeSpan(StringBuilder result, string content, ref int i, int end)
    {
        var hadLeftSpace = result.Length > 0 && IsHorizontalWs(result[result.Length - 1]);

        // Drop any horizontal indentation already emitted for this span's line.
        while (result.Length > 0 && IsHorizontalWs(result[result.Length - 1]))
        {
            result.Length--;
        }
        var leftIsLineStart = result.Length == 0 || result[result.Length - 1] == '\n';

        var j = end + 1;
        var hadRightSpace = j < content.Length && IsHorizontalWs(content[j]);
        while (j < content.Length && IsHorizontalWs(content[j]))
        {
            j++;
        }
        var rightIsLineEnd = j >= content.Length || content[j] == '\n';

        if (leftIsLineStart && rightIsLineEnd)
        {
            // Envelope occupied its own line: consume one trailing newline so no blank line is
            // left behind.
            if (j < content.Length && content[j] == '\n')
            {
                j++;
            }
        }
        else if (hadLeftSpace || hadRightSpace)
        {
            // Inline removal between words: keep exactly one separating space.
            if (result.Length > 0 && result[result.Length - 1] != '\n' &&
                j < content.Length && content[j] != '\n')
            {
                result.Append(' ');
            }
        }

        i = j;
    }

    private static bool IsHorizontalWs(char c) => c == ' ' || c == '\t';

    /// <summary>
    /// True when the span content[start..end] is alone on its line(s): only horizontal
    /// whitespace separates it from a line boundary (start/end of content or a newline) on both
    /// sides.
    /// </summary>
    private static bool IsStandaloneSpan(string s, int start, int end)
    {
        var l = start - 1;
        while (l >= 0 && IsHorizontalWs(s[l])) l--;
        var leftOk = l < 0 || s[l] == '\n';

        var r = end + 1;
        while (r < s.Length && IsHorizontalWs(s[r])) r++;
        var rightOk = r >= s.Length || s[r] == '\n';

        return leftOk && rightOk;
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

    private enum EnvelopeKind
    {
        /// <summary>Not a tool-call envelope; always preserved.</summary>
        None,

        /// <summary>
        /// Unambiguous protocol envelope (explicit tool_call/tool_calls key). Always removed,
        /// wherever it appears.
        /// </summary>
        Definitive,

        /// <summary>
        /// Has the call SHAPE (name-like + args-like, or a bare tool/function marker) but no
        /// definitive key. Removed only when it stands alone on its own line.
        /// </summary>
        Marker
    }

    private static EnvelopeKind ClassifyEnvelope(string json)
    {
        // Match keys followed by a colon so we do not trip on the same word appearing as a
        // value or inside prose.
        bool HasKey(string key) =>
            Regex.IsMatch(json, "\"" + Regex.Escape(key) + "\"\\s*:", RegexOptions.IgnoreCase);

        // Explicit tool-call protocol container: an unambiguous artifact regardless of context.
        if (HasKey("tool_call") || HasKey("tool_calls"))
        {
            return EnvelopeKind.Definitive;
        }

        var hasName = HasKey("name") || HasKey("tool") || HasKey("function");

        // Concrete invocation shape: a name-like key plus literal argument VALUES
        // ("arguments"/"args"). This is the runtime tool-call payload that leaks into output, so
        // it is stripped wherever it appears (inline or standalone).
        if (hasName && (HasKey("arguments") || HasKey("args")))
        {
            return EnvelopeKind.Definitive;
        }

        // Ambiguous call shape: a name-like key plus a "parameters" object. That is ALSO how a
        // function/tool is DEFINED in a JSON schema (which a user may legitimately paste and ask
        // about), so it is only stripped when it stands alone on its own line - never inside a
        // sentence.
        if (hasName && HasKey("parameters"))
        {
            return EnvelopeKind.Marker;
        }

        // Bare protocol marker: a lone tool/function identifier with no args (e.g.
        // {"tool":"read_file"}). Old sanitizers removed these; keep removing them, but only when
        // they stand alone so a JSON literal discussed in prose is never touched.
        if (HasKey("tool") || HasKey("function"))
        {
            return EnvelopeKind.Marker;
        }

        return EnvelopeKind.None;
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
