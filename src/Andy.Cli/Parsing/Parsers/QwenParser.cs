using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Andy.Cli.Services;

namespace Andy.Cli.Parsing.Parsers;

/// <summary>
/// Parser specifically for Qwen model responses
/// Handles the {"tool_call": {"name": "...", "arguments": {...}}} format
/// </summary>
public class QwenParser : BaseParser
{
    // Qwen's specific tool call formats (both nested and direct)
    private static readonly Regex QwenToolCallPattern = new(
        @"\{[^{}]*""tool_call""\s*:\s*\{[^{}]*""name""\s*:\s*""([^""]+)""[^{}]*\}[^{}]*\}",
        RegexOptions.Singleline | RegexOptions.Compiled);
    
    // Direct tool format: {"tool":"name","parameters":{...}}
    private static readonly Regex DirectToolPattern = new(
        @"\{\s*""tool""\s*:\s*""([^""]+)""\s*,\s*""parameters""\s*:\s*\{[^{}]*\}\s*\}",
        RegexOptions.Singleline | RegexOptions.Compiled);
    
    // Qwen sometimes outputs internal thoughts with specific markers
    private static readonly Regex ThoughtPattern = new(
        @"(?:<thinking>|<thought>|<internal>|【[^】]+】|\[\[.*?\]\])(.*?)(?:</thinking>|</thought>|</internal>)",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // Garbage text patterns specific to Qwen
    private static readonly Regex GarbagePattern = new(
        @"Y[\u200B-\u200F\uFEFF]*ou\b|^\s*[{}]\s*$|""contents""\s*:\s*\[|""recursive""\s*:",
        RegexOptions.Multiline | RegexOptions.Compiled);
    
    // Pattern to remove any remaining tool JSON from display
    private static readonly Regex AnyToolJsonPattern = new(
        @"\{[^{}]*""(?:tool|tool_call|function|name)""[^{}]*\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public QwenParser(IJsonRepairService jsonRepair, ILogger<QwenParser>? logger = null)
        : base(jsonRepair, logger)
    {
    }

    public override ResponseNode Parse(string response, ParserContext? context = null)
    {
        var root = new ResponseNode
        {
            ModelProvider = context?.ModelProvider ?? "qwen",
            ModelName = context?.ModelName ?? "unknown"
        };
        
        if (string.IsNullOrWhiteSpace(response))
        {
            _logger?.LogDebug("Empty response received");
            return root;
        }
        
        _logger?.LogDebug("Parsing Qwen response of length {Length}", response.Length);
        
        // Extract and remove tool calls first
        var (toolCalls, cleanedText) = ExtractToolCalls(response);
        
        // Add tool calls to AST
        foreach (var toolCall in toolCalls)
        {
            root.Children.Add(toolCall);
        }
        
        // Extract and handle thoughts (if preserving)
        if (context?.PreserveThoughts == true)
        {
            var thoughts = ExtractThoughts(cleanedText);
            foreach (var thought in thoughts)
            {
                root.Children.Add(thought);
                // Remove thought from text
                cleanedText = cleanedText.Replace(thought.Metadata["original"]?.ToString() ?? "", "");
            }
        }
        else
        {
            // Just remove thoughts from text
            cleanedText = ThoughtPattern.Replace(cleanedText, "");
        }
        
        // Clean garbage text
        cleanedText = CleanGarbageText(cleanedText);
        
        // Extract code blocks
        var codeBlocks = ExtractCodeBlocks(cleanedText);
        foreach (var code in codeBlocks)
        {
            root.Children.Add(code);
            // Remove code block from text
            cleanedText = cleanedText.Substring(0, code.StartPosition) + 
                         cleanedText.Substring(code.EndPosition);
        }
        
        // Extract semantic elements
        ExtractSemanticElements(root, cleanedText, context);
        
        // Add remaining text as text nodes
        if (!string.IsNullOrWhiteSpace(cleanedText))
        {
            var textNode = new TextNode
            {
                Content = cleanedText.Trim(),
                Format = DetectTextFormat(cleanedText)
            };
            root.Children.Add(textNode);
        }
        
        // Set metadata
        root.ResponseMetadata.IsComplete = !response.Contains("[INCOMPLETE]") && 
                                          !response.EndsWith("...");
        
        return root;
    }

    private (List<ToolCallNode>, string) ExtractToolCalls(string response)
    {
        var toolCalls = new List<ToolCallNode>();
        var cleanedText = response;
        var seenCalls = new HashSet<string>();
        
        // Try nested tool_call format first
        var matches = QwenToolCallPattern.Matches(response);
        
        foreach (Match match in matches)
        {
            try
            {
                var json = match.Value;
                _logger?.LogDebug("Found nested tool call JSON: {Json}", json);
                
                // Use JSON repair to handle malformed JSON
                var parsed = _jsonRepair.SafeParse<Dictionary<string, object?>>(json);
                
                if (parsed?.TryGetValue("tool_call", out var toolCallObj) == true)
                {
                    var toolCall = ParseToolCallObject(toolCallObj);
                    if (toolCall != null)
                    {
                        // Create unique key to prevent duplicates
                        var callKey = $"{toolCall.ToolName}:{JsonSerializer.Serialize(toolCall.Arguments)}";
                        if (seenCalls.Add(callKey))
                        {
                            toolCall.StartPosition = match.Index;
                            toolCall.EndPosition = match.Index + match.Length;
                            toolCalls.Add(toolCall);
                            _logger?.LogDebug("Extracted tool call: {ToolName}", toolCall.ToolName);
                        }
                    }
                }
                
                // Remove tool call JSON from text
                cleanedText = cleanedText.Replace(match.Value, "");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse nested tool call: {Match}", match.Value);
            }
        }
        
        // Try direct tool format: {"tool":"name","parameters":{...}}
        var directMatches = DirectToolPattern.Matches(cleanedText);
        
        foreach (Match match in directMatches)
        {
            try
            {
                var json = match.Value;
                _logger?.LogDebug("Found direct tool JSON: {Json}", json);
                
                // Fix malformed JSON (missing parameter names)
                var fixedJson = FixMalformedToolJson(json);
                
                // Use JSON repair to handle malformed JSON
                var parsed = _jsonRepair.SafeParse<Dictionary<string, object?>>(fixedJson);
                
                if (parsed != null && parsed.TryGetValue("tool", out var toolNameObj))
                {
                    var toolName = toolNameObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        var toolCall = new ToolCallNode
                        {
                            CallId = "call_" + Guid.NewGuid().ToString("N"),
                            ToolName = toolName,
                            Arguments = new Dictionary<string, object?>()
                        };
                        
                        if (parsed.TryGetValue("parameters", out var paramsObj))
                        {
                            toolCall.Arguments = ExtractArguments(paramsObj);
                        }
                        
                        // Create unique key to prevent duplicates
                        var callKey = $"{toolCall.ToolName}:{JsonSerializer.Serialize(toolCall.Arguments)}";
                        if (seenCalls.Add(callKey))
                        {
                            toolCall.StartPosition = match.Index;
                            toolCall.EndPosition = match.Index + match.Length;
                            toolCalls.Add(toolCall);
                            _logger?.LogDebug("Extracted direct tool call: {ToolName}", toolName);
                        }
                    }
                }
                
                // Remove tool call JSON from text
                cleanedText = cleanedText.Replace(match.Value, "");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse direct tool call: {Match}", match.Value);
            }
        }
        
        // Final cleanup: remove any remaining tool-related JSON
        cleanedText = AnyToolJsonPattern.Replace(cleanedText, "");
        
        return (toolCalls, cleanedText);
    }

    private ToolCallNode? ParseToolCallObject(object? toolCallObj)
    {
        if (toolCallObj == null)
            return null;
        
        try
        {
            Dictionary<string, object?>? dict = null;
            
            // Handle JsonElement
            if (toolCallObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                dict = new Dictionary<string, object?>();
                foreach (var prop in je.EnumerateObject())
                {
                    dict[prop.Name] = ConvertJsonElement(prop.Value);
                }
            }
            // Handle Dictionary
            else if (toolCallObj is Dictionary<string, object?> d)
            {
                dict = d;
            }
            
            if (dict == null)
                return null;
            
            // Extract tool name
            if (!dict.TryGetValue("name", out var nameObj) || 
                string.IsNullOrWhiteSpace(nameObj?.ToString()))
            {
                _logger?.LogDebug("No tool name found in tool call object");
                return null;
            }
            
            var toolCall = new ToolCallNode
            {
                CallId = "call_" + Guid.NewGuid().ToString("N"),
                ToolName = nameObj.ToString()!,
                Arguments = new Dictionary<string, object?>()
            };
            
            // Extract arguments
            if (dict.TryGetValue("arguments", out var argsObj))
            {
                toolCall.Arguments = ExtractArguments(argsObj);
            }
            
            return toolCall;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse tool call object");
            return null;
        }
    }

    private Dictionary<string, object?> ExtractArguments(object? argsObj)
    {
        var args = new Dictionary<string, object?>();
        
        if (argsObj == null)
            return args;
        
        // Handle JsonElement
        if (argsObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in je.EnumerateObject())
            {
                args[prop.Name] = ConvertJsonElement(prop.Value);
            }
        }
        // Handle Dictionary
        else if (argsObj is Dictionary<string, object?> dict)
        {
            args = dict;
        }
        // Handle string (might be JSON string)
        else if (argsObj is string str)
        {
            var parsed = _jsonRepair.SafeParse<Dictionary<string, object?>>(str);
            if (parsed != null)
            {
                args = parsed;
            }
        }
        
        return args;
    }

    private List<ThoughtNode> ExtractThoughts(string text)
    {
        var thoughts = new List<ThoughtNode>();
        var matches = ThoughtPattern.Matches(text);
        
        foreach (Match match in matches)
        {
            var thought = new ThoughtNode
            {
                Content = match.Groups[1].Value.Trim(),
                ShouldHide = true,
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length
            };
            thought.Metadata["original"] = match.Value;
            thoughts.Add(thought);
        }
        
        return thoughts;
    }

    private string FixMalformedToolJson(string json)
    {
        // Fix common Qwen malformations like {"path":"/path",false} -> {"path":"/path","recursive":false}
        // This is a simple heuristic fix for the most common case
        if (json.Contains(",false}") || json.Contains(",true}"))
        {
            // Try to infer the missing parameter name
            if (json.Contains("list_directory") || json.Contains("path"))
            {
                json = json.Replace(",false}", ",\"recursive\":false}")
                          .Replace(",true}", ",\"recursive\":true}");
            }
        }
        
        return json;
    }
    
    private string CleanGarbageText(string text)
    {
        // Remove Qwen-specific garbage patterns
        text = GarbagePattern.Replace(text, "");
        
        // Remove common AI fluff phrases
        text = Regex.Replace(text, 
            @"(?i)^\\s*(?:Let me|I'll|I need to|Now I'll|I will|I'm going to)[^.!?]*[.!?:]*", 
            "", RegexOptions.Multiline);
        
        // Clean up excessive whitespace
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        
        return text.Trim();
    }

    private TextFormat DetectTextFormat(string text)
    {
        // Check for markdown indicators
        if (Regex.IsMatch(text, @"^#{1,6}\s", RegexOptions.Multiline) ||
            Regex.IsMatch(text, @"^\*\s|^-\s|^\d+\.\s", RegexOptions.Multiline) ||
            text.Contains("**") || text.Contains("*") || text.Contains("`"))
        {
            return TextFormat.Markdown;
        }
        
        // Check for JSON
        if ((text.TrimStart().StartsWith("{") && text.TrimEnd().EndsWith("}")) ||
            (text.TrimStart().StartsWith("[") && text.TrimEnd().EndsWith("]")))
        {
            return TextFormat.Json;
        }
        
        return TextFormat.Plain;
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
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.ToString()
        };
    }

    public override ParserCapabilities GetCapabilities()
    {
        return new ParserCapabilities
        {
            SupportsStreaming = true,
            SupportsToolCalls = true,
            SupportsCodeBlocks = true,
            SupportsMarkdown = true,
            SupportsFileReferences = true,
            SupportsQuestions = true,
            SupportsThoughts = true,
            SupportedFormats = new List<string> { "json", "markdown", "plain" }
        };
    }
}