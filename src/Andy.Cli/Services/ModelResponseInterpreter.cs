using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Andy.Llm.Models;

namespace Andy.Cli.Services;

/// <summary>
/// Interprets LLM responses based on the model being used
/// </summary>
public interface IModelResponseInterpreter
{
    /// <summary>
    /// Extract tool calls from the model's response
    /// </summary>
    List<ModelToolCall> ExtractToolCalls(string response, string modelName, string provider);
    
    /// <summary>
    /// Format tool results for sending back to the model
    /// </summary>
    string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results, string modelName, string provider);
    
    /// <summary>
    /// Check if the response contains fake tool results
    /// </summary>
    bool ContainsFakeToolResults(string response, string modelName);
    
    /// <summary>
    /// Clean response text for display
    /// </summary>
    string CleanResponseForDisplay(string response, string modelName);
}

public class ModelResponseInterpreter : IModelResponseInterpreter
{
    private readonly Dictionary<string, IModelInterpreter> _interpreters;
    
    public ModelResponseInterpreter()
    {
        _interpreters = new Dictionary<string, IModelInterpreter>(StringComparer.OrdinalIgnoreCase)
        {
            ["llama"] = new LlamaInterpreter(),
            ["qwen"] = new QwenInterpreter(),
            ["gpt"] = new GptInterpreter(),
            ["o1"] = new O1Interpreter(),  // OpenAI o1 models use thinking tags
            ["claude"] = new ClaudeInterpreter(),
            ["gemini"] = new GeminiInterpreter(),
            ["mistral"] = new MistralInterpreter(),
            ["deepseek"] = new DeepseekInterpreter(),
            ["gemma"] = new GemmaInterpreter(),
            ["mixtral"] = new MistralInterpreter(),
            ["phi"] = new PhiInterpreter(),
        };
    }
    
    public List<ModelToolCall> ExtractToolCalls(string response, string modelName, string provider)
    {
        var interpreter = GetInterpreter(modelName);
        return interpreter.ExtractToolCalls(response);
    }
    
    public string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results, string modelName, string provider)
    {
        var interpreter = GetInterpreter(modelName);
        return interpreter.FormatToolResults(toolCalls, results);
    }
    
    public bool ContainsFakeToolResults(string response, string modelName)
    {
        var interpreter = GetInterpreter(modelName);
        return interpreter.ContainsFakeToolResults(response);
    }
    
    public string CleanResponseForDisplay(string response, string modelName)
    {
        var interpreter = GetInterpreter(modelName);
        return interpreter.CleanResponseForDisplay(response);
    }
    
    private IModelInterpreter GetInterpreter(string modelName)
    {
        modelName = modelName?.ToLowerInvariant() ?? "";
        
        // Try to match by prefix
        foreach (var kvp in _interpreters)
        {
            if (modelName.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }
        
        // Default to GPT interpreter for unknown models
        return _interpreters["gpt"];
    }
}

/// <summary>
/// Base interface for model-specific interpreters
/// </summary>
internal interface IModelInterpreter
{
    List<ModelToolCall> ExtractToolCalls(string response);
    string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results);
    bool ContainsFakeToolResults(string response);
    string CleanResponseForDisplay(string response);
}

/// <summary>
/// Base implementation with common functionality
/// </summary>
internal abstract class BaseModelInterpreter : IModelInterpreter
{
    public abstract List<ModelToolCall> ExtractToolCalls(string response);
    public abstract string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results);
    
    public virtual bool ContainsFakeToolResults(string response)
    {
        return response.Contains("[Tool Results]", StringComparison.OrdinalIgnoreCase) ||
               response.Contains("Tool execution result", StringComparison.OrdinalIgnoreCase);
    }
    
    public virtual string CleanResponseForDisplay(string response)
    {
        // Remove special tags used by various models
        response = RemoveSpecialTags(response);
        
        // Remove common internal thoughts
        var patterns = new[]
        {
            @"I'll.*?tool\.",
            @"Let me.*?for you\.",
            @"I need to.*?\.",
            @"I'm going to.*?\.",
            @"Now I'll.*?\.",
        };
        
        foreach (var pattern in patterns)
        {
            response = Regex.Replace(response, pattern, "", RegexOptions.IgnoreCase);
        }
        
        return response.Trim();
    }
    
    /// <summary>
    /// Remove special tags like <thinking>, <reflection>, etc.
    /// </summary>
    protected virtual string RemoveSpecialTags(string response)
    {
        // Common thinking/reasoning tags used by various models
        var tagsToRemove = new[]
        {
            "thinking",      // Claude, some fine-tuned models
            "reflection",    // Some reasoning models
            "reasoning",     // Reasoning models
            "step",         // Step-by-step reasoning
            "plan",         // Planning tags
            "scratch",      // Scratchpad thinking
            "internal",     // Internal thoughts
            "thought",      // Thought process
            "analysis"      // Analysis tags
        };
        
        foreach (var tag in tagsToRemove)
        {
            // Remove both self-closing and paired tags
            response = Regex.Replace(response, $@"<{tag}[^>]*?/>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            response = Regex.Replace(response, $@"<{tag}[^>]*?>.*?</{tag}>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }
        
        return response;
    }
    
    protected ModelToolCall? ParseToolCallJson(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("tool", out var toolElement))
                return null;
                
            var toolId = toolElement.GetString();
            if (string.IsNullOrEmpty(toolId))
                return null;
                
            var parameters = new Dictionary<string, object?>();
            
            if (root.TryGetProperty("parameters", out var paramsElement))
            {
                foreach (var prop in paramsElement.EnumerateObject())
                {
                    parameters[prop.Name] = ParseJsonValue(prop.Value);
                }
            }
            
            return new ModelToolCall
            {
                ToolId = toolId,
                Parameters = parameters
            };
        }
        catch
        {
            return null;
        }
    }
    
    protected object? ParseJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ParseJsonValue).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ParseJsonValue(p.Value)),
            _ => element.ToString()
        };
    }
}

/// <summary>
/// Interpreter for Llama models
/// </summary>
internal class LlamaInterpreter : BaseModelInterpreter
{
    public override List<ModelToolCall> ExtractToolCalls(string response)
    {
        var toolCalls = new List<ModelToolCall>();
        
        // Llama typically uses clean JSON format
        // Look for JSON blocks
        var jsonPattern = @"```json\s*(\{.*?\})\s*```";
        var matches = Regex.Matches(response, jsonPattern, RegexOptions.Singleline);
        
        foreach (Match match in matches)
        {
            var json = match.Groups[1].Value;
            var toolCall = ParseToolCallJson(json);
            if (toolCall != null)
            {
                toolCalls.Add(toolCall);
            }
        }
        
        // Also try direct JSON if no code blocks found
        if (toolCalls.Count == 0)
        {
            var directJsonPattern = @"\{[^{}]*""tool""\s*:\s*""[^""]+""[^{}]*\}";
            matches = Regex.Matches(response, directJsonPattern);
            
            foreach (Match match in matches)
            {
                var toolCall = ParseToolCallJson(match.Value);
                if (toolCall != null)
                {
                    toolCalls.Add(toolCall);
                }
            }
        }
        
        return toolCalls;
    }
    
    public override string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        // Llama expects simple format
        var formatted = new List<string>();
        for (int i = 0; i < toolCalls.Count && i < results.Count; i++)
        {
            formatted.Add($"[Tool: {toolCalls[i].ToolId}]\n{results[i]}");
        }
        return string.Join("\n\n", formatted);
    }
}

/// <summary>
/// Interpreter for Qwen models
/// </summary>
internal class QwenInterpreter : BaseModelInterpreter
{
    public override List<ModelToolCall> ExtractToolCalls(string response)
    {
        var toolCalls = new List<ModelToolCall>();
        
        // Primary: Qwen SHOULD use <tool_call> tags
        var toolCallPattern = @"<tool_call>\s*(\{.*?\})\s*</tool_call>";
        var matches = Regex.Matches(response, toolCallPattern, RegexOptions.Singleline);
        
        foreach (Match match in matches)
        {
            var json = match.Groups[1].Value;
            
            // Parse the Qwen-specific format: {"name": "function_name", "arguments": {...}}
            try
            {
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("name", out var nameElement) &&
                    root.TryGetProperty("arguments", out var argsElement))
                {
                    var toolCall = new ModelToolCall
                    {
                        ToolId = nameElement.GetString() ?? "",
                        Parameters = new Dictionary<string, object?>()
                    };
                    
                    // Parse arguments
                    if (argsElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in argsElement.EnumerateObject())
                        {
                            toolCall.Parameters[prop.Name] = ParseJsonValue(prop.Value);
                        }
                    }
                    else if (argsElement.ValueKind == JsonValueKind.String)
                    {
                        // Arguments might be JSON string
                        var argsJson = argsElement.GetString();
                        if (!string.IsNullOrEmpty(argsJson))
                        {
                            var argsDoc = JsonDocument.Parse(argsJson);
                            foreach (var prop in argsDoc.RootElement.EnumerateObject())
                            {
                                toolCall.Parameters[prop.Name] = ParseJsonValue(prop.Value);
                            }
                        }
                    }
                    
                    toolCalls.Add(toolCall);
                }
            }
            catch
            {
                // Try fallback patterns if primary format fails
            }
        }
        
        // Fallback: Qwen sometimes uses the generic format
        if (toolCalls.Count == 0)
        {
            // Look for the exact format Qwen is outputting: {"tool":"...","parameters":{...}}
            // This pattern now properly handles empty parameters {}
            var genericPattern = @"\{\s*""tool""\s*:\s*""([^""]+)""\s*,\s*""parameters""\s*:\s*(\{[^}]*\})\s*\}";
            matches = Regex.Matches(response, genericPattern, RegexOptions.Singleline);
            
            foreach (Match match in matches)
            {
                var toolId = match.Groups[1].Value;
                var paramsJson = match.Groups[2].Value;
                
                try
                {
                    var toolCall = new ModelToolCall
                    {
                        ToolId = toolId,
                        Parameters = new Dictionary<string, object?>()
                    };
                    
                    // Parse parameters if not empty
                    if (paramsJson != null && paramsJson.Trim() != "{}")
                    {
                        var paramsDoc = JsonDocument.Parse(paramsJson);
                        foreach (var prop in paramsDoc.RootElement.EnumerateObject())
                        {
                            toolCall.Parameters[prop.Name] = ParseJsonValue(prop.Value);
                        }
                    }
                    
                    toolCalls.Add(toolCall);
                }
                catch
                {
                    // Parsing failed, try next match
                }
            }
        }
        
        // Final fallback: try other patterns
        if (toolCalls.Count == 0)
        {
            var patterns = new[]
            {
                @"`+json?\s*(\{.*?\})\s*`+",  // Backticks with optional json marker
                @"```json\s*(\{.*?\})\s*```",  // Triple backticks with json marker
            };
            
            foreach (var pattern in patterns)
            {
                matches = Regex.Matches(response, pattern, RegexOptions.Singleline);
                foreach (Match match in matches)
                {
                    var json = match.Groups[1].Value;
                    var toolCall = ParseToolCallJson(json);
                    if (toolCall != null)
                    {
                        toolCalls.Add(toolCall);
                        break;
                    }
                }
                
                if (toolCalls.Count > 0)
                    break;
            }
        }
        
        return toolCalls;
    }
    
    public override string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        // Qwen needs cleaner tool results without JSON formatting
        // It tends to output raw tool results if they're too complex
        var formatted = new List<string>();
        for (int i = 0; i < toolCalls.Count && i < results.Count; i++)
        {
            var result = results[i];
            
            // Try to simplify JSON results for Qwen
            if (result.TrimStart().StartsWith("{") || result.TrimStart().StartsWith("["))
            {
                try
                {
                    // Try to parse and format more simply
                    var doc = System.Text.Json.JsonDocument.Parse(result);
                    var simplified = new System.Text.StringBuilder();
                    simplified.AppendLine($"[{toolCalls[i].ToolId} completed successfully]");
                    
                    // Extract key information if possible
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        // For list_directory, extract items
                        if (toolCalls[i].ToolId == "list_directory" && doc.RootElement.TryGetProperty("items", out var items))
                        {
                            simplified.AppendLine("Found items:");
                            foreach (var item in items.EnumerateArray())
                            {
                                if (item.TryGetProperty("name", out var name))
                                {
                                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : "unknown";
                                    simplified.AppendLine($"  - {name.GetString()} ({type})");
                                }
                            }
                        }
                        // For other tools, show key properties
                        else
                        {
                            foreach (var prop in doc.RootElement.EnumerateObject().Take(5))
                            {
                                simplified.AppendLine($"  {prop.Name}: {prop.Value}");
                            }
                        }
                    }
                    formatted.Add(simplified.ToString());
                }
                catch
                {
                    // If JSON parsing fails, use simplified format
                    formatted.Add($"[{toolCalls[i].ToolId} completed]\nResult: {result.Substring(0, Math.Min(200, result.Length))}...");
                }
            }
            else
            {
                // Non-JSON results can be used as-is
                formatted.Add($"[{toolCalls[i].ToolId} result]\n{result}");
            }
        }
        return string.Join("\n\n", formatted);
    }
    
    public override string CleanResponseForDisplay(string response)
    {
        // Remove escaped JSON output that Qwen sometimes produces
        // This happens when Qwen tries to output tool results as escaped strings
        if (response.Contains(@"\u0022") || response.Contains(@"\\n") || response.Contains("[Tool Execution:"))
        {
            // This is likely Qwen outputting escaped JSON - remove it entirely
            response = Regex.Replace(response, @""".*?\[Tool Execution:.*?""", "", RegexOptions.Singleline);
            response = Regex.Replace(response, @"\\u[0-9a-fA-F]{4}", "", RegexOptions.Singleline);
            response = Regex.Replace(response, @"\\n", "\n", RegexOptions.Singleline);
            response = Regex.Replace(response, @"\\""", "\"", RegexOptions.Singleline);
            
            // If the entire response is escaped JSON, clear it
            if (response.Trim().StartsWith("\"") && response.Trim().EndsWith("\""))
            {
                response = "";
            }
        }
        
        // Remove [Tool Results] and any JSON that follows it - Qwen sometimes outputs fake results
        if (response.Contains("[Tool Results]", StringComparison.OrdinalIgnoreCase))
        {
            response = Regex.Replace(response, @"\[Tool Results\][\s\S]*?(?=\n\n[A-Za-z]|\z)", "", 
                RegexOptions.IgnoreCase);
        }
        
        // Remove tool_call tags and their content
        response = Regex.Replace(response, @"<tool_call>.*?</tool_call>", "", RegexOptions.Singleline);
        
        // Remove the generic tool format that Qwen sometimes outputs directly
        // This is the format: {"tool":"...","parameters":{...}}
        response = Regex.Replace(response, @"\{\s*""tool""\s*:\s*""[^""]+""\s*,\s*""parameters""\s*:\s*\{[^}]*\}\s*\}", "", 
            RegexOptions.Singleline);
        
        // Remove the duplicated JSON that Qwen sometimes outputs
        response = Regex.Replace(response, @"`+json?\s*\{.*?\}\s*`+", "", RegexOptions.Singleline);
        response = Regex.Replace(response, @"```json\s*\{.*?\}\s*```", "", RegexOptions.Singleline);
        
        // Remove instructional reminders that Qwen sometimes includes
        // Remove "Please remember to use..." paragraphs
        response = Regex.Replace(response, @"Please remember to use.*?(?=\n\n|\z)", "", 
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        // Remove tool format examples that start with backticks
        response = Regex.Replace(response, @"`\s*\{[^`]*""tool""\s*:[^`]*\}\s*`", "", 
            RegexOptions.Singleline);
        
        // Remove "Go ahead and get started" type phrases
        response = Regex.Replace(response, @"Go ahead and get started[!.]?", "", 
            RegexOptions.IgnoreCase);
        
        // Remove "For example, if you want to..." instructional sentences
        response = Regex.Replace(response, @"For example,? if you want to.*?(?:\.|$)", "", 
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        // Remove "Please wait for the results..." that Qwen adds after tool calls
        response = Regex.Replace(response, @"Please wait for the results\.{3,}", "", 
            RegexOptions.IgnoreCase);
        
        // Clean up any double line breaks left behind
        response = Regex.Replace(response, @"\n{3,}", "\n\n");
        
        // IMPORTANT: Remove duplicate paragraphs that Qwen sometimes outputs
        response = RemoveDuplicateParagraphs(response);
        
        var cleaned = base.CleanResponseForDisplay(response).Trim();
        
        // If the response is ONLY a tool call (no other text), return a default message
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            // Check if the original had tool calls
            var toolCalls = ExtractToolCalls(response);
            if (toolCalls.Any())
            {
                // Return empty string - let the tool execution speak for itself
                return "";
            }
        }
        
        return cleaned;
    }
    
    private string RemoveDuplicateParagraphs(string text)
    {
        // First, handle the specific pattern where Qwen duplicates partial lines
        // Like: "The project...feat\nThe project...feat\nures..."
        var lines = text.Split('\n');
        var cleanedLines = new List<string>();
        string previousLine = "";
        
        foreach (var line in lines)
        {
            // Skip if this line is identical to the previous one
            if (line == previousLine && !string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            
            // Skip if this line starts with the same content as the previous line
            // (handles the case where text is duplicated mid-word)
            if (!string.IsNullOrWhiteSpace(previousLine) && 
                !string.IsNullOrWhiteSpace(line) &&
                line.Length > 50 && 
                previousLine.Length > 50 &&
                line.StartsWith(previousLine.Substring(0, Math.Min(50, previousLine.Length))))
            {
                // This might be a duplicate
                continue;
            }
            
            cleanedLines.Add(line);
            previousLine = line;
        }
        
        text = string.Join("\n", cleanedLines);
        
        // Now handle paragraph-level duplicates
        var paragraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var uniqueParagraphs = new List<string>();
        var seenParagraphs = new HashSet<string>(StringComparer.Ordinal);
        
        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.Trim();
            // Normalize internal line breaks for comparison
            var normalized = Regex.Replace(trimmed, @"\s+", " ");
            
            if (!string.IsNullOrWhiteSpace(trimmed) && !seenParagraphs.Contains(normalized))
            {
                uniqueParagraphs.Add(trimmed);
                seenParagraphs.Add(normalized);
            }
        }
        
        return string.Join("\n\n", uniqueParagraphs);
    }
}

/// <summary>
/// Interpreter for GPT models
/// </summary>
internal class GptInterpreter : BaseModelInterpreter
{
    public override List<ModelToolCall> ExtractToolCalls(string response)
    {
        var toolCalls = new List<ModelToolCall>();
        
        // GPT uses clean JSON in code blocks
        var jsonPattern = @"```json\s*(\{.*?\})\s*```";
        var matches = Regex.Matches(response, jsonPattern, RegexOptions.Singleline);
        
        foreach (Match match in matches)
        {
            var json = match.Groups[1].Value;
            var toolCall = ParseToolCallJson(json);
            if (toolCall != null)
            {
                toolCalls.Add(toolCall);
            }
        }
        
        return toolCalls;
    }
    
    public override string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        // GPT expects structured format
        var formatted = new List<string>();
        for (int i = 0; i < toolCalls.Count && i < results.Count; i++)
        {
            formatted.Add($"Tool: {toolCalls[i].ToolId}\nResult: {results[i]}");
        }
        return string.Join("\n\n", formatted);
    }
}

/// <summary>
/// Interpreter for Claude models
/// </summary>
internal class ClaudeInterpreter : BaseModelInterpreter
{
    public override List<ModelToolCall> ExtractToolCalls(string response)
    {
        var toolCalls = new List<ModelToolCall>();
        
        // Claude uses <tool_use> tags or JSON blocks
        var toolUsePattern = @"<tool_use>\s*(\{.*?\})\s*</tool_use>";
        var matches = Regex.Matches(response, toolUsePattern, RegexOptions.Singleline);
        
        foreach (Match match in matches)
        {
            var json = match.Groups[1].Value;
            var toolCall = ParseToolCallJson(json);
            if (toolCall != null)
            {
                toolCalls.Add(toolCall);
            }
        }
        
        // Fallback to JSON blocks
        if (toolCalls.Count == 0)
        {
            var jsonPattern = @"```json\s*(\{.*?\})\s*```";
            matches = Regex.Matches(response, jsonPattern, RegexOptions.Singleline);
            
            foreach (Match match in matches)
            {
                var json = match.Groups[1].Value;
                var toolCall = ParseToolCallJson(json);
                if (toolCall != null)
                {
                    toolCalls.Add(toolCall);
                }
            }
        }
        
        return toolCalls;
    }
    
    public override string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        // Claude expects tool_result format
        var formatted = new List<string>();
        for (int i = 0; i < toolCalls.Count && i < results.Count; i++)
        {
            formatted.Add($"<tool_result>\nTool: {toolCalls[i].ToolId}\n{results[i]}\n</tool_result>");
        }
        return string.Join("\n", formatted);
    }
}

/// <summary>
/// Interpreter for Gemini models
/// </summary>
internal class GeminiInterpreter : BaseModelInterpreter
{
    public override List<ModelToolCall> ExtractToolCalls(string response)
    {
        // Similar to GPT
        return new GptInterpreter().ExtractToolCalls(response);
    }
    
    public override string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        // Similar to GPT
        return new GptInterpreter().FormatToolResults(toolCalls, results);
    }
}

/// <summary>
/// Interpreter for Mistral/Mixtral models
/// </summary>
internal class MistralInterpreter : BaseModelInterpreter
{
    public override List<ModelToolCall> ExtractToolCalls(string response)
    {
        // Similar to Llama
        return new LlamaInterpreter().ExtractToolCalls(response);
    }
    
    public override string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        // Similar to Llama
        return new LlamaInterpreter().FormatToolResults(toolCalls, results);
    }
}

/// <summary>
/// Interpreter for Deepseek models
/// </summary>
internal class DeepseekInterpreter : BaseModelInterpreter
{
    public override List<ModelToolCall> ExtractToolCalls(string response)
    {
        // Similar to Qwen for code models
        return new QwenInterpreter().ExtractToolCalls(response);
    }
    
    public override string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        // Similar to Qwen
        return new QwenInterpreter().FormatToolResults(toolCalls, results);
    }
}

/// <summary>
/// Interpreter for Gemma models
/// </summary>
internal class GemmaInterpreter : BaseModelInterpreter
{
    public override List<ModelToolCall> ExtractToolCalls(string response)
    {
        // Similar to Llama
        return new LlamaInterpreter().ExtractToolCalls(response);
    }
    
    public override string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        // Similar to Llama
        return new LlamaInterpreter().FormatToolResults(toolCalls, results);
    }
}

/// <summary>
/// Interpreter for OpenAI o1 models (reasoning models)
/// </summary>
internal class O1Interpreter : GptInterpreter
{
    protected override string RemoveSpecialTags(string response)
    {
        // o1 models use <thinking> tags extensively
        // First remove thinking tags specifically
        response = Regex.Replace(response, @"<thinking>.*?</thinking>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        
        // Then apply base tag removal
        return base.RemoveSpecialTags(response);
    }
}

/// <summary>
/// Interpreter for Phi models
/// </summary>
internal class PhiInterpreter : BaseModelInterpreter
{
    public override List<ModelToolCall> ExtractToolCalls(string response)
    {
        // Phi models typically use clean JSON similar to GPT
        return new GptInterpreter().ExtractToolCalls(response);
    }
    
    public override string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        return new GptInterpreter().FormatToolResults(toolCalls, results);
    }
}

public class ModelToolCall
{
    public string ToolId { get; set; } = "";
    public Dictionary<string, object?> Parameters { get; set; } = new();
}