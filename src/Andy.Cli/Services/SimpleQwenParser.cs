using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// Simplified Qwen response parser that handles only the actual formats Qwen uses
/// </summary>
public class SimpleQwenParser : IQwenResponseParser
{
    private readonly IJsonRepairService _jsonRepair;
    private readonly ILogger<SimpleQwenParser>? _logger;

    // Simple pattern for Qwen's tool_call format: {"tool_call": {"name": "...", "arguments": {...}}}
    private static readonly Regex QwenToolCallPattern = new(
        @"\{[^{}]*""tool_call""\s*:\s*\{[^{}]*""name""\s*:\s*""([^""]+)""[^{}]*\}[^{}]*\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Pattern to remove garbage text with invisible characters
    private static readonly Regex GarbageYouPattern = new(
        @"Y[\u200B-\u200F\uFEFF]*ou\b",
        RegexOptions.Compiled);

    // Pattern to remove raw JSON output that looks like tool results
    private static readonly Regex RawJsonResultPattern = new(
        @"""[^""]+"":\s*(?:\[[^\]]*\]|""[^""]*""|true|false|null|\d+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public SimpleQwenParser(
        IJsonRepairService jsonRepair,
        StreamingToolCallAccumulator? accumulator,
        ILogger<SimpleQwenParser>? logger = null)
    {
        _jsonRepair = jsonRepair;
        _logger = logger;
    }

    public ParsedResponse Parse(string response)
    {
        var result = new ParsedResponse();

        if (string.IsNullOrWhiteSpace(response))
        {
            _logger?.LogDebug("Empty response received");
            return result;
        }

        _logger?.LogDebug("Parsing response of length {Length}", response.Length);

        // Extract tool calls
        result.ToolCalls = ExtractToolCalls(response);
        _logger?.LogDebug("Found {Count} tool calls", result.ToolCalls.Count);

        // Clean the text
        result.TextContent = CleanResponseText(response);
        _logger?.LogDebug("Cleaned text length: {Length}", result.TextContent.Length);

        result.Metadata = new ResponseMetadata
        {
            IsComplete = true,
            FinishReason = "complete"
        };

        return result;
    }

    public List<ModelToolCall> ExtractToolCalls(string response)
    {
        var toolCalls = new List<ModelToolCall>();

        if (string.IsNullOrWhiteSpace(response))
            return toolCalls;

        // Look for Qwen's specific tool_call format
        var matches = QwenToolCallPattern.Matches(response);
        var seenCalls = new HashSet<string>(); // Prevent duplicates

        foreach (Match match in matches)
        {
            try
            {
                var json = match.Value;
                _logger?.LogDebug("Attempting to parse tool call JSON: {Json}", json);

                // Use JSON repair to handle malformed JSON
                var parsed = _jsonRepair.SafeParse<Dictionary<string, object?>>(json);

                if (parsed?.TryGetValue("tool_call", out var toolCallObj) == true)
                {
                    var toolCall = ExtractToolCallFromObject(toolCallObj);
                    if (toolCall != null)
                    {
                        // Create a unique key to prevent duplicates
                        var callKey = $"{toolCall.ToolId}:{JsonSerializer.Serialize(toolCall.Parameters)}";
                        if (seenCalls.Add(callKey))
                        {
                            toolCalls.Add(toolCall);
                            _logger?.LogDebug("Successfully extracted tool call: {ToolId}", toolCall.ToolId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse tool call from match: {Match}", match.Value);
            }
        }

        return toolCalls;
    }

    private ModelToolCall? ExtractToolCallFromObject(object? toolCallObj)
    {
        if (toolCallObj == null)
            return null;

        try
        {
            Dictionary<string, object?>? toolCallDict = null;

            // Handle JsonElement
            if (toolCallObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                toolCallDict = new Dictionary<string, object?>();
                foreach (var prop in je.EnumerateObject())
                {
                    toolCallDict[prop.Name] = ConvertJsonElement(prop.Value);
                }
            }
            // Handle Dictionary
            else if (toolCallObj is Dictionary<string, object?> dict)
            {
                toolCallDict = dict;
            }

            if (toolCallDict == null)
                return null;

            // Extract tool name
            string? toolName = null;
            if (toolCallDict.TryGetValue("name", out var nameObj))
                toolName = nameObj?.ToString();

            if (string.IsNullOrWhiteSpace(toolName))
            {
                _logger?.LogDebug("No tool name found in tool call object");
                return null;
            }

            // Extract arguments
            var parameters = new Dictionary<string, object?>();
            if (toolCallDict.TryGetValue("arguments", out var argsObj))
            {
                parameters = ExtractParameters(argsObj);
            }

            return new ModelToolCall
            {
                ToolId = toolName,
                Parameters = parameters
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to extract tool call from object");
            return null;
        }
    }

    private Dictionary<string, object?> ExtractParameters(object? argsObj)
    {
        var parameters = new Dictionary<string, object?>();

        if (argsObj == null)
            return parameters;

        // Handle JsonElement
        if (argsObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in je.EnumerateObject())
            {
                parameters[prop.Name] = ConvertJsonElement(prop.Value);
            }
        }
        // Handle Dictionary
        else if (argsObj is Dictionary<string, object?> dict)
        {
            parameters = dict;
        }
        // Handle string (might be JSON string)
        else if (argsObj is string str)
        {
            var parsed = _jsonRepair.SafeParse<Dictionary<string, object?>>(str);
            if (parsed != null)
            {
                parameters = parsed;
            }
        }

        return parameters;
    }

    public string CleanResponseText(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "";

        var cleaned = response;

        // Remove tool call JSON
        cleaned = QwenToolCallPattern.Replace(cleaned, "");

        // Remove garbage "You" with invisible characters
        cleaned = GarbageYouPattern.Replace(cleaned, "");

        // Remove stray braces
        cleaned = Regex.Replace(cleaned, @"^\s*[{}]\s*$", "", RegexOptions.Multiline);

        // Remove raw JSON that looks like tool results
        if (cleaned.Contains("\"contents\"") || cleaned.Contains("\"recursive\"") ||
            cleaned.Contains("\"include_hidden\"") || cleaned.Contains("\"sort_by\""))
        {
            // This looks like raw tool result output - remove it
            cleaned = RawJsonResultPattern.Replace(cleaned, "");
        }

        // Remove common AI fluff phrases
        cleaned = Regex.Replace(cleaned, @"(?i)^\s*(?:Let me|I'll|I need to|Now I'll|Try this:)[^.!?]*[.!?:]*", "", RegexOptions.Multiline);

        // Clean up whitespace
        cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        cleaned = cleaned.Trim();

        return cleaned;
    }

    public async Task<ParsedResponse> ParseStreamingAsync(
        IAsyncEnumerable<string> chunks,
        CancellationToken cancellationToken = default)
    {
        // For now, just accumulate and parse at the end
        var fullResponse = "";
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            fullResponse += chunk;
        }
        return Parse(fullResponse);
    }

    private object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.ToString()
        };
    }
}