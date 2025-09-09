using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Andy.Llm.Models;

namespace Andy.Cli.Services;

/// <summary>
/// Parsed response from Qwen model
/// </summary>
public class ParsedResponse
{
    public string TextContent { get; set; } = "";
    public List<ModelToolCall> ToolCalls { get; set; } = new();
    public ResponseMetadata Metadata { get; set; } = new();
    public List<ParseError> Errors { get; set; } = new();
    public bool HasToolCalls => ToolCalls.Any();
    public bool HasErrors => Errors.Any();
}

/// <summary>
/// Response metadata
/// </summary>
public class ResponseMetadata
{
    public int TotalChunks { get; set; }
    public TimeSpan ProcessingTime { get; set; }
    public string? FinishReason { get; set; }
    public bool IsComplete { get; set; }
    public int TokenCount { get; set; }
}

/// <summary>
/// Parse error information
/// </summary>
public class ParseError
{
    public string Message { get; set; } = "";
    public string? Context { get; set; }
    public int? Position { get; set; }
    public ParseErrorType Type { get; set; }
}

public enum ParseErrorType
{
    InvalidJson,
    MissingToolName,
    MalformedToolCall,
    IncompleteResponse,
    UnexpectedFormat
}

/// <summary>
/// Parser for Qwen model responses with robust error handling
/// Based on qwen-code's parsing patterns
/// </summary>
public interface IQwenResponseParser
{
    /// <summary>
    /// Parse a complete response string
    /// </summary>
    ParsedResponse Parse(string response);

    /// <summary>
    /// Parse streaming response chunks
    /// </summary>
    Task<ParsedResponse> ParseStreamingAsync(
        IAsyncEnumerable<string> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extract tool calls from a response
    /// </summary>
    List<ModelToolCall> ExtractToolCalls(string response);

    /// <summary>
    /// Clean response text for display
    /// </summary>
    string CleanResponseText(string response);
}

public class QwenResponseParser : IQwenResponseParser
{
    private readonly IJsonRepairService _jsonRepair;
    private readonly StreamingToolCallAccumulator _accumulator;
    private readonly ILogger<QwenResponseParser>? _logger;

    // Regex patterns for different Qwen response formats
    private static readonly Regex ToolCallTagPattern = new(
        @"<tool_call>\s*(.*?)\s*</tool_call>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsonBlockPattern = new(
        @"```(?:json)?\s*(\{.*?\})\s*```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Updated pattern to handle Qwen's tool_call format and regular formats
    private static readonly Regex DirectJsonPattern = new(
        @"(\{[\s\S]*?""(?:tool_call|name|tool|function)""[\s\S]*?\})",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // New: explicit tool_call object matcher (multiple occurrences)
    private static readonly Regex ToolCallObjectPattern = new(
        @"\{\s*""tool_call""\s*:\s*\{[\s\S]*?\}\s*\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ThinkingTagPattern = new(
        @"<thinking>.*?</thinking>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InternalThoughtPattern = new(
        @"(?:(?:I'll\s+(?:use|search|check|run)\b)|(?:Let me\s+(?:check|search|look|run)\b)|(?:I need to\s+(?:examine|check|analyze)\b)|(?:I'm going to\s+(?:analyze|check|use|call|run)\b)|(?:Now I'll\s+\w+)|(?:Searching for|Looking for|Checking\b)|(?:Going to\s+(?:analyze|check))|(?:Try this)|(?:Let me\s+help\s+you\s+with\s+that)(?!\s*task)|(?:I can\s+assist\b))[^.!?]*[.!?:]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ToolResultsPattern = new(
        @"\[Tool Results\][\s\S]*?(?=\n\n|$)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex GarbageTextPattern = new(
        @"^Y[\u200C\u200D\u200E\u200F\uFEFF]+ou\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex StrayBracesPattern = new(
        @"^\s*[{}]\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public QwenResponseParser(
        IJsonRepairService jsonRepair,
        StreamingToolCallAccumulator accumulator,
        ILogger<QwenResponseParser>? logger = null)
    {
        _jsonRepair = jsonRepair;
        _accumulator = accumulator;
        _logger = logger;
    }

    public ParsedResponse Parse(string response)
    {
        var startTime = DateTime.UtcNow;
        var result = new ParsedResponse();

        if (string.IsNullOrWhiteSpace(response))
        {
            result.Errors.Add(new ParseError
            {
                Message = "Empty response",
                Type = ParseErrorType.IncompleteResponse
            });
            return result;
        }

        // Extract tool calls and track errors
        result.ToolCalls = ExtractToolCalls(response);

        // Check if we had tool call tags but failed to parse them
        var toolCallMatches = ToolCallTagPattern.Matches(response);
        if (toolCallMatches.Count > 0 && !result.ToolCalls.Any())
        {
            result.Errors.Add(new ParseError
            {
                Message = "Found tool call tags but failed to parse JSON content",
                Type = ParseErrorType.InvalidJson,
                Context = response.Length > 100 ? response.Substring(0, 100) + "..." : response
            });
        }

        // Clean and extract text content
        result.TextContent = CleanResponseText(response);

        // Set metadata
        result.Metadata = new ResponseMetadata
        {
            TotalChunks = 1,
            ProcessingTime = DateTime.UtcNow - startTime,
            IsComplete = true,
            FinishReason = "complete"
        };

        _logger?.LogDebug("Parsed response: {ToolCount} tool calls, {TextLength} chars of text",
            result.ToolCalls.Count, result.TextContent.Length);

        return result;
    }

    public async Task<ParsedResponse> ParseStreamingAsync(
        IAsyncEnumerable<string> chunks,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var result = new ParsedResponse();
        var textBuilder = new StringBuilder();
        var chunkCount = 0;
        var isInToolCall = false;
        var toolCallBuffer = new StringBuilder();

        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            chunkCount++;

            // Check if chunk contains tool call markers
            if (chunk.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase))
            {
                isInToolCall = true;
                toolCallBuffer.Clear();

                // Extract the start of tool call content
                var startIdx = chunk.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase) + "<tool_call>".Length;
                if (startIdx < chunk.Length)
                {
                    toolCallBuffer.Append(chunk.Substring(startIdx));
                }
            }
            else if (isInToolCall)
            {
                // Check for end of tool call
                if (chunk.Contains("</tool_call>", StringComparison.OrdinalIgnoreCase))
                {
                    var endIdx = chunk.IndexOf("</tool_call>", StringComparison.OrdinalIgnoreCase);
                    if (endIdx > 0)
                    {
                        toolCallBuffer.Append(chunk.Substring(0, endIdx));
                    }

                    // Process the complete tool call
                    ProcessToolCallJson(toolCallBuffer.ToString(), result);
                    isInToolCall = false;

                    // Add any remaining text after the tool call
                    var remainingStart = endIdx + "</tool_call>".Length;
                    if (remainingStart < chunk.Length)
                    {
                        textBuilder.Append(chunk.Substring(remainingStart));
                    }
                }
                else
                {
                    // Continue accumulating tool call content
                    toolCallBuffer.Append(chunk);
                }
            }
            else
            {
                // Regular text content
                textBuilder.Append(chunk);

                // Also check for inline JSON tool calls
                var inlineToolCalls = ExtractInlineToolCalls(chunk);
                if (inlineToolCalls.Any())
                {
                    result.ToolCalls.AddRange(inlineToolCalls);
                }
            }

            // Create stream chunk for accumulator
            var streamChunk = ConvertToStreamChunk(chunk, isInToolCall);
            if (streamChunk != null)
            {
                _accumulator.AccumulateChunk(streamChunk);
            }
        }

        // Get any completed tool calls from accumulator
        var accumulatedCalls = _accumulator.GetCompletedCalls();
        if (accumulatedCalls.Any())
        {
            result.ToolCalls.AddRange(accumulatedCalls);
        }

        // Clean and set text content
        result.TextContent = CleanResponseText(textBuilder.ToString());

        // Set metadata
        result.Metadata = new ResponseMetadata
        {
            TotalChunks = chunkCount,
            ProcessingTime = DateTime.UtcNow - startTime,
            IsComplete = true,
            FinishReason = "stream_complete"
        };

        _logger?.LogDebug("Parsed streaming response: {ChunkCount} chunks, {ToolCount} tool calls, {TextLength} chars",
            chunkCount, result.ToolCalls.Count, result.TextContent.Length);

        return result;
    }

    public List<ModelToolCall> ExtractToolCalls(string response)
    {
        var toolCalls = new List<ModelToolCall>();
        var seenCalls = new HashSet<string>(); // Track unique tool calls to avoid duplicates

        // 1. Try to extract from <tool_call> tags (Qwen's preferred format)
        var toolCallMatches = ToolCallTagPattern.Matches(response);
        foreach (Match match in toolCallMatches)
        {
            var json = match.Groups[1].Value;
            var toolCall = ParseToolCallJson(json);
            if (toolCall != null)
            {
                var callKey = $"{toolCall.ToolId}:{JsonSerializer.Serialize(toolCall.Parameters)}";
                if (seenCalls.Add(callKey)) // Only add if not seen before
                {
                    toolCalls.Add(toolCall);
                }
            }
            else
            {
                _logger?.LogDebug("Failed to parse tool call JSON from tag: {Json}", json);
            }
        }

        // 2. If no tool calls found, try JSON blocks
        if (!toolCalls.Any())
        {
            var jsonBlockMatches = JsonBlockPattern.Matches(response);
            foreach (Match match in jsonBlockMatches)
            {
                var json = match.Groups[1].Value;
                var toolCall = ParseToolCallJson(json);
                if (toolCall != null)
                {
                    var callKey = $"{toolCall.ToolId}:{CreateCanonicalParamsKey(toolCall.Parameters)}";
                    if (seenCalls.Add(callKey))
                    {
                        toolCalls.Add(toolCall);
                    }
                }
                else
                {
                    _logger?.LogDebug("Failed to parse tool call JSON from block: {Json}", json);
                }
            }
        }

        // 3. If still no tool calls, try direct JSON patterns
        if (!toolCalls.Any())
        {
            // First, match explicit tool_call objects (most reliable and may appear multiple times)
            var toolCallObjects = ToolCallObjectPattern.Matches(response);
            foreach (Match m in toolCallObjects)
            {
                var toolCall = ParseToolCallJson(m.Value);
                if (toolCall != null)
                {
                    var callKey = $"{toolCall.ToolId}:{CreateCanonicalParamsKey(toolCall.Parameters)}";
                    if (seenCalls.Add(callKey))
                    {
                        toolCalls.Add(toolCall);
                    }
                }
            }

            var directJsonMatches = DirectJsonPattern.Matches(response);

            // Special handling for Qwen's strange format with stray colon between objects and stray braces
            var cleanedResponse = Regex.Replace(response, @"}\s*:\s*\{", "}{"); // Fix patterns like "}:\n{"
            cleanedResponse = cleanedResponse.Replace("}\n{", "}{"); // Fix separated JSON objects
            cleanedResponse = Regex.Replace(cleanedResponse, @"^\s*}\s*$", "", RegexOptions.Multiline); // Remove standalone closing braces

            directJsonMatches = DirectJsonPattern.Matches(cleanedResponse);
            foreach (Match match in directJsonMatches)
            {
                var json = match.Groups[1].Value;
                if (json.IndexOf("tool_call", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Already handled by ToolCallObjectPattern; skip to avoid duplicates
                    continue;
                }
                var toolCall = ParseToolCallJson(json);
                if (toolCall != null)
                {
                    var callKey = $"{toolCall.ToolId}:{CreateCanonicalParamsKey(toolCall.Parameters)}";
                    if (seenCalls.Add(callKey))
                    {
                        toolCalls.Add(toolCall);
                    }
                }
                else
                {
                    _logger?.LogDebug("Failed to parse tool call JSON from direct pattern: {Json}", json);
                }
            }

            // Also scan for balanced JSON objects and try each
            foreach (var json in FindBalancedJsonObjects(cleanedResponse))
            {
                if (json.IndexOf("tool_call", StringComparison.OrdinalIgnoreCase) < 0 &&
                    json.IndexOf("\"name\"", StringComparison.OrdinalIgnoreCase) < 0 &&
                    json.IndexOf("\"tool\"", StringComparison.OrdinalIgnoreCase) < 0 &&
                    json.IndexOf("\"function\"", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                // Skip JSONs that contain explicit tool_call (already handled)
                if (json.IndexOf("tool_call", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                var toolCall = ParseToolCallJson(json);
                if (toolCall != null)
                {
                    var callKey = $"{toolCall.ToolId}:{CreateCanonicalParamsKey(toolCall.Parameters)}";
                    if (seenCalls.Add(callKey))
                    {
                        toolCalls.Add(toolCall);
                    }
                }
            }

            // Iterative extraction using helper, removing consumed JSON (skip explicit tool_call JSONs)
            var remaining = cleanedResponse;
            int guard = 0;
            while (guard++ < 10)
            {
                var json = remaining.ExtractToolCallJson();
                if (string.IsNullOrWhiteSpace(json)) break;
                if (json.IndexOf("tool_call", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Already accounted for; remove and continue
                    var rmIdx = remaining.IndexOf(json, StringComparison.Ordinal);
                    remaining = rmIdx >= 0 ? remaining.Remove(rmIdx, json.Length) : remaining;
                    continue;
                }
                var toolCall = ParseToolCallJson(json);
                if (toolCall != null)
                {
                    var callKey = $"{toolCall.ToolId}:{CreateCanonicalParamsKey(toolCall.Parameters)}";
                    if (seenCalls.Add(callKey))
                    {
                        toolCalls.Add(toolCall);
                    }
                }
                var idx = remaining.IndexOf(json, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    remaining = remaining.Remove(idx, json.Length);
                }
                else
                {
                    break;
                }
            }
        }

        // Final de-duplication using canonical parameter serialization
        var unique = new List<ModelToolCall>();
        var finalSeen = new HashSet<string>();
        foreach (var call in toolCalls)
        {
            var key = $"{call.ToolId}:{BuildCanonicalJson(call.Parameters)}";
            if (finalSeen.Add(key))
            {
                unique.Add(call);
            }
        }

        return unique;
    }

    public string CleanResponseText(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "";

        var cleaned = response;

        // Remove garbage text patterns (like "Y‌ou" with invisible characters)
        cleaned = GarbageTextPattern.Replace(cleaned, "");

        // Remove explicit tool_call object blocks first, before altering braces
        cleaned = ToolCallObjectPattern.Replace(cleaned, "");

        // Remove any balanced JSON objects that contain a tool_call key
        cleaned = RemoveJsonObjectsByKey(cleaned, "tool_call");

        // Now remove stray braces on their own lines (often from Qwen's output)
        cleaned = StrayBracesPattern.Replace(cleaned, "");

        // Remove raw JSON that looks like tool results (Qwen sometimes outputs raw tool results)
        // Pattern for raw JSON with quotes around field names (like "contents", "recursive", etc.)
        cleaned = Regex.Replace(cleaned, "\"[^\\\"]+\"\\s*:\\s*(?:\\[[^\\]]*\\]|\"[^\\\"]*\"|true|false|null|\\d+)", "", RegexOptions.Multiline);

        // Remove [Tool Results] sections that contain raw JSON
        cleaned = ToolResultsPattern.Replace(cleaned, "");

        // Remove any remaining object-valued JSON fields like "subdirectories": { ... }
        cleaned = Regex.Replace(cleaned, "\\\"[^\\\"]+\\\"\\s*:\\s*\\{[\\s\\S]*?\\}", "", RegexOptions.Singleline);

        // Explicitly remove tool_call key-value blocks if any remain
        cleaned = Regex.Replace(cleaned, @"""tool_call""\s*:\s*\{[\s\S]*?\}", "", RegexOptions.Singleline);

        // Remove tool call tags but preserve outer non-JSON text
        cleaned = Regex.Replace(cleaned, @"<tool_call>\s*\{[\s\S]*?\}\s*</tool_call>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"</?tool_call>", "", RegexOptions.IgnoreCase);

        // Remove JSON blocks (preserve surrounding text better)
        cleaned = JsonBlockPattern.Replace(cleaned, "");

        // Remove thinking tags
        cleaned = ThinkingTagPattern.Replace(cleaned, "");

        // Remove internal thoughts (model's reasoning about tool usage) but keep neutral helpful sentences
        cleaned = InternalThoughtPattern.Replace(cleaned, "");

        // Remove direct JSON tool calls (only if they look like tool calls)
        // Be more cautious about removing JSON that might not be tool calls
        cleaned = DirectJsonPattern.Replace(cleaned, "");

        // Clean up excessive whitespace but preserve line breaks initially
        cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");  // Multiple spaces/tabs to single space
        cleaned = Regex.Replace(cleaned, @"\n\s*\n", "\n"); // Multiple newlines to single newline
        cleaned = Regex.Replace(cleaned, @"^\s+|\s+$", "", RegexOptions.Multiline); // Trim each line

        // Drop punctuation-only lines left after JSON removal
        var lines = cleaned.Split('\n');
        var filtered = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0) { filtered.Add(""); continue; }
            if (Regex.IsMatch(t, @"^[,:\[\]{}""']+$"))
                continue;
            filtered.Add(line);
        }
        cleaned = string.Join("\n", filtered);

        return cleaned.Trim();
    }

    private ModelToolCall? ParseToolCallJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            // Use JsonRepairService to handle malformed JSON
            var parsed = _jsonRepair.SafeParse<Dictionary<string, object?>>(json);

            if (parsed == null)
            {
                _logger?.LogDebug("Failed to parse tool call JSON even after repair: {Json}", json);
                return null;
            }

            // Check if this is a Qwen-style tool_call wrapper
            if (parsed.TryGetValue("tool_call", out var toolCallObj))
            {
                // Extract from nested tool_call object
                if (toolCallObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    // Convert JsonElement to Dictionary
                    var nestedDict = new Dictionary<string, object?>();
                    foreach (var prop in je.EnumerateObject())
                    {
                        nestedDict[prop.Name] = ConvertJsonElement(prop.Value);
                    }
                    parsed = nestedDict;
                }
                else if (toolCallObj is Dictionary<string, object?> dict)
                {
                    parsed = dict;
                }
            }

            // Extract tool name (handle different field names)
            string? toolName = null;
            if (parsed.TryGetValue("name", out var nameObj))
                toolName = nameObj?.ToString();
            else if (parsed.TryGetValue("tool", out var toolObj))
                toolName = toolObj?.ToString();
            else if (parsed.TryGetValue("function", out var funcObj))
                toolName = funcObj?.ToString();

            if (string.IsNullOrWhiteSpace(toolName))
            {
                _logger?.LogDebug("No tool name found in parsed JSON");
                return null;
            }

            // Extract arguments/parameters
            var parameters = new Dictionary<string, object?>();

            if (parsed.TryGetValue("arguments", out var argsObj))
            {
                parameters = ExtractParametersFromObject(argsObj);
            }
            else if (parsed.TryGetValue("parameters", out var paramsObj))
            {
                parameters = ExtractParametersFromObject(paramsObj);
            }
            else
            {
                // All other fields might be parameters
                foreach (var kvp in parsed)
                {
                    if (kvp.Key != "name" && kvp.Key != "tool" && kvp.Key != "function")
                    {
                        parameters[kvp.Key] = kvp.Value;
                    }
                }
            }

            return new ModelToolCall
            {
                ToolId = toolName,
                Parameters = parameters
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error parsing tool call JSON");
            return null;
        }
    }

    private void ProcessToolCallJson(string json, ParsedResponse result)
    {
        var toolCall = ParseToolCallJson(json);
        if (toolCall != null)
        {
            result.ToolCalls.Add(toolCall);
        }
        else
        {
            result.Errors.Add(new ParseError
            {
                Message = "Failed to parse tool call JSON",
                Context = json.Length > 100 ? json.Substring(0, 100) + "..." : json,
                Type = ParseErrorType.InvalidJson
            });
        }
    }

    private List<ModelToolCall> ExtractInlineToolCalls(string chunk)
    {
        var toolCalls = new List<ModelToolCall>();

        // Check for inline JSON patterns
        var matches = DirectJsonPattern.Matches(chunk);
        foreach (Match match in matches)
        {
            var toolCall = ParseToolCallJson(match.Groups[1].Value);
            if (toolCall != null)
            {
                toolCalls.Add(toolCall);
            }
        }

        return toolCalls;
    }

    private StreamChunk? ConvertToStreamChunk(string content, bool isToolCall)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        return new StreamChunk
        {
            Content = isToolCall ? null : content,
            ToolCallArguments = isToolCall ? content : null,
            IsFinished = false
        };
    }

    private Dictionary<string, object?> ExtractParametersFromObject(object? obj)
    {
        var parameters = new Dictionary<string, object?>();

        if (obj is Dictionary<string, object?> dict)
        {
            parameters = dict;
        }
        else if (obj is System.Text.Json.JsonElement element)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    parameters[property.Name] = ConvertJsonElement(property.Value);
                }
            }
        }
        else if (obj is string str)
        {
            // Arguments might be JSON string
            var parsed = _jsonRepair.SafeParse<Dictionary<string, object?>>(str);
            if (parsed != null)
            {
                parameters = parsed;
            }
        }

        return parameters;
    }

    private object? ConvertJsonElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal : element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element
        };
    }

    private string CreateCanonicalParamsKey(Dictionary<string, object?> parameters)
    {
        if (parameters == null || parameters.Count == 0) return "{}";
        try
        {
            var ordered = parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in ordered)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(kv.Key);
                sb.Append(':');
                sb.Append(kv.Value?.ToString());
            }
            sb.Append('}');
            return sb.ToString();
        }
        catch
        {
            return JsonSerializer.Serialize(parameters);
        }
    }

    private string BuildCanonicalJson(Dictionary<string, object?> parameters)
    {
        try
        {
            var ordered = parameters
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return JsonSerializer.Serialize(ordered);
        }
        catch
        {
            return JsonSerializer.Serialize(parameters);
        }
    }

    // Utility: find balanced top-level JSON objects in a string
    private IEnumerable<string> FindBalancedJsonObjects(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        int depth = 0;
        int start = -1;
        bool inString = false;
        bool escaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (c == '\\') { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return text.Substring(start, i - start + 1);
                    start = -1;
                }
            }
        }
    }

    private string RemoveJsonObjectsByKey(string text, string key)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var current = text;
        int guard = 0;
        while (guard++ < 20)
        {
            var objects = FindBalancedJsonObjects(current).ToList();
            var toRemove = objects.FirstOrDefault(o => o.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase) >= 0);
            if (string.IsNullOrEmpty(toRemove)) break;
            var idx = current.IndexOf(toRemove, StringComparison.Ordinal);
            if (idx < 0) break;
            current = current.Remove(idx, toRemove.Length);
        }
        return current;
    }
}