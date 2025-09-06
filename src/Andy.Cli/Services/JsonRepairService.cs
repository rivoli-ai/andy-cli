using System;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// Service for repairing and parsing malformed JSON, particularly from streaming LLM responses
/// </summary>
public interface IJsonRepairService
{
    /// <summary>
    /// Safely parse JSON string with repair fallback for malformed JSON
    /// </summary>
    T? SafeParse<T>(string json, T? fallback = default);
    
    /// <summary>
    /// Try to repair malformed JSON
    /// </summary>
    bool TryRepairJson(string malformedJson, out string repairedJson);
    
    /// <summary>
    /// Check if a string appears to be complete JSON
    /// </summary>
    bool IsCompleteJson(string json);
    
    /// <summary>
    /// Attempt to parse JSON without repair (for performance when JSON is known to be valid)
    /// </summary>
    bool TryParseWithoutRepair<T>(string json, out T? result);
}

public class JsonRepairService : IJsonRepairService
{
    private readonly ILogger<JsonRepairService>? _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonRepairService(ILogger<JsonRepairService>? logger = null)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
    }

    public T? SafeParse<T>(string json, T? fallback = default)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            _logger?.LogDebug("Empty JSON string, returning fallback");
            return fallback;
        }

        // First attempt: try normal JSON.Parse
        if (TryParseWithoutRepair<T>(json, out var result))
        {
            return result;
        }

        // Second attempt: use JsonRepairSharp to fix common JSON issues
        try
        {
            var repairedJson = JsonRepairSharp.JsonRepair.RepairJson(json);
            
            if (repairedJson != json)
            {
                _logger?.LogDebug("JSON was repaired. Original length: {OrigLen}, Repaired length: {RepLen}", 
                    json.Length, repairedJson.Length);
            }

            result = JsonSerializer.Deserialize<T>(repairedJson, _jsonOptions);
            
            if (result != null)
            {
                _logger?.LogDebug("Successfully parsed repaired JSON");
                return result;
            }
        }
        catch (Exception repairEx)
        {
            _logger?.LogWarning(repairEx, "Failed to parse JSON even after repair attempt. JSON snippet: {Json}", 
                json.Length > 200 ? json.Substring(0, 200) + "..." : json);
        }

        return fallback;
    }

    public bool TryRepairJson(string malformedJson, out string repairedJson)
    {
        repairedJson = malformedJson;
        
        if (string.IsNullOrWhiteSpace(malformedJson))
        {
            return false;
        }

        try
        {
            repairedJson = JsonRepairSharp.JsonRepair.RepairJson(malformedJson);
            
            // Validate that the repaired JSON is actually valid
            using var doc = JsonDocument.Parse(repairedJson);
            
            _logger?.LogDebug("Successfully repaired JSON");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to repair JSON");
            repairedJson = malformedJson;
            return false;
        }
    }

    public bool IsCompleteJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        json = json.Trim();
        
        // Quick checks for obviously incomplete JSON
        if (!json.StartsWith('{') && !json.StartsWith('['))
        {
            return false;
        }

        // Count brackets and braces
        int braceDepth = 0;
        int bracketDepth = 0;
        bool inString = false;
        bool escaped = false;

        foreach (char c in json)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            switch (c)
            {
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
            }
        }

        // JSON is complete if all brackets/braces are balanced
        bool isComplete = braceDepth == 0 && bracketDepth == 0 && !inString;
        
        if (!isComplete)
        {
            _logger?.LogDebug("Incomplete JSON detected. Brace depth: {BraceDepth}, Bracket depth: {BracketDepth}, In string: {InString}", 
                braceDepth, bracketDepth, inString);
        }

        return isComplete;
    }

    public bool TryParseWithoutRepair<T>(string json, out T? result)
    {
        result = default;
        
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            result = JsonSerializer.Deserialize<T>(json, _jsonOptions);
            return result != null;
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "JSON parse failed without repair");
            return false;
        }
    }
}

/// <summary>
/// Extension methods for JSON repair functionality
/// </summary>
public static class JsonRepairExtensions
{
    /// <summary>
    /// Try to extract tool call JSON from various formats that Qwen models might produce
    /// </summary>
    public static string? ExtractToolCallJson(this string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        // Try to find JSON within <tool_call> tags first (Qwen's preferred format)
        var toolCallStart = response.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
        var toolCallEnd = response.IndexOf("</tool_call>", StringComparison.OrdinalIgnoreCase);
        
        if (toolCallStart >= 0 && toolCallEnd > toolCallStart)
        {
            var startIdx = toolCallStart + "<tool_call>".Length;
            return response.Substring(startIdx, toolCallEnd - startIdx).Trim();
        }

        // Look for JSON object pattern
        var jsonStart = response.IndexOf('{');
        if (jsonStart >= 0)
        {
            // Find the matching closing brace
            int braceCount = 0;
            int i = jsonStart;
            
            for (; i < response.Length; i++)
            {
                if (response[i] == '{')
                    braceCount++;
                else if (response[i] == '}')
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        return response.Substring(jsonStart, i - jsonStart + 1);
                    }
                }
            }
            
            // If we didn't find a matching brace, return what we have (incomplete JSON)
            if (braceCount > 0)
            {
                return response.Substring(jsonStart);
            }
        }

        return null;
    }
}