using System.Collections.Generic;
using System.Linq;
using Andy.Tools.Core;

namespace Andy.Cli.Services;

/// <summary>
/// Maps common parameter name variations to the correct parameter names expected by tools
/// </summary>
public static class ParameterMapper
{
    // Common parameter name mappings
    private static readonly Dictionary<string, Dictionary<string, string>> ToolParameterMappings = new()
    {
        ["read_file"] = new Dictionary<string, string>
        {
            ["path"] = "file_path",
            ["filepath"] = "file_path",
            ["filename"] = "file_path",
            ["file"] = "file_path"
        },
        ["write_file"] = new Dictionary<string, string>
        {
            ["path"] = "file_path",
            ["filepath"] = "file_path",
            ["filename"] = "file_path",
            ["file"] = "file_path",
            ["data"] = "content",
            ["text"] = "content",
            ["body"] = "content",
            ["contents"] = "content"
        },
        ["copy_file"] = new Dictionary<string, string>
        {
            ["source"] = "source_path",
            ["src"] = "source_path",
            ["from"] = "source_path",
            ["destination"] = "destination_path",
            ["dest"] = "destination_path",
            ["dst"] = "destination_path",
            ["to"] = "destination_path",
            ["target"] = "destination_path"
        },
        ["move_file"] = new Dictionary<string, string>
        {
            ["source"] = "source_path",
            ["src"] = "source_path",
            ["from"] = "source_path",
            ["destination"] = "destination_path",
            ["dest"] = "destination_path",
            ["dst"] = "destination_path",
            ["to"] = "destination_path",
            ["target"] = "destination_path"
        },
        ["delete_file"] = new Dictionary<string, string>
        {
            ["path"] = "file_path",
            ["filepath"] = "file_path",
            ["filename"] = "file_path",
            ["file"] = "file_path"
        },
        ["list_directory"] = new Dictionary<string, string>
        {
            ["directory"] = "path",
            ["dir"] = "path",
            ["folder"] = "path",
            ["location"] = "path"
        },
        ["create_directory"] = new Dictionary<string, string>
        {
            ["directory"] = "path",
            ["dir"] = "path",
            ["folder"] = "path",
            ["name"] = "path"
        }
    };

    /// <summary>
    /// Maps parameter names from what the LLM provided to what the tool expects
    /// </summary>
    public static Dictionary<string, object?> MapParameters(
        string toolId,
        Dictionary<string, object?> inputParameters,
        ToolMetadata toolMetadata)
    {
        var mappedParameters = new Dictionary<string, object?>();

        // Get the mapping for this specific tool if it exists
        var hasToolMapping = ToolParameterMappings.TryGetValue(toolId, out var mappings);

        // Get expected parameter names from tool metadata
        var expectedParams = toolMetadata.Parameters.ToDictionary(p => p.Name.ToLower(), p => p.Name);

        // Create a map of parameter metadata for type checking
        var paramMetadata = toolMetadata.Parameters.ToDictionary(p => p.Name.ToLower(), p => p);

        foreach (var kvp in inputParameters)
        {
            var paramName = kvp.Key;
            var paramValue = kvp.Value;
            string finalParamName = paramName;

            // First, check if it's already the correct name
            if (expectedParams.ContainsKey(paramName.ToLower()))
            {
                finalParamName = expectedParams[paramName.ToLower()];
            }
            // Try tool-specific mappings
            else if (hasToolMapping && mappings!.TryGetValue(paramName.ToLower(), out var mappedName))
            {
                // Verify the mapped name is actually expected by the tool
                if (expectedParams.ContainsKey(mappedName.ToLower()))
                {
                    finalParamName = expectedParams[mappedName.ToLower()];
                }
            }
            // Try fuzzy matching as last resort
            else
            {
                var fuzzyMatch = FindFuzzyMatch(paramName, expectedParams.Keys);
                if (fuzzyMatch != null)
                {
                    finalParamName = expectedParams[fuzzyMatch];
                }
            }

            // Apply type conversion if needed
            if (paramMetadata.TryGetValue(finalParamName.ToLower(), out var metadata))
            {
                paramValue = ConvertParameterType(paramValue, metadata);
            }

            mappedParameters[finalParamName] = paramValue;
        }

        return mappedParameters;
    }

    /// <summary>
    /// Converts a parameter value to the expected type
    /// </summary>
    private static object? ConvertParameterType(object? value, ToolParameter metadata)
    {
        if (value == null) return null;

        var expectedType = metadata.Type?.ToLower() ?? "string";

        // Handle array conversions
        if (expectedType == "array" || expectedType.Contains("[]"))
        {
            // If value is already an array, return as-is
            if (value is Array || value is System.Collections.IList)
            {
                return value;
            }

            // If it's a string that looks like JSON array, try to parse it
            if (value is string strValue)
            {
                strValue = strValue.Trim();
                if (strValue.StartsWith("[") && strValue.EndsWith("]"))
                {
                    try
                    {
                        return System.Text.Json.JsonSerializer.Deserialize<object[]>(strValue);
                    }
                    catch
                    {
                        // If JSON parsing fails, wrap single value in array
                        return new[] { value };
                    }
                }

                // For comma-separated values
                if (strValue.Contains(","))
                {
                    return strValue.Split(',').Select(s => s.Trim()).ToArray();
                }

                // Single value - wrap in array
                return new[] { strValue };
            }

            // For any other type, wrap in array
            return new[] { value };
        }

        // Handle boolean conversions
        if (expectedType == "boolean" || expectedType == "bool")
        {
            if (value is bool) return value;
            if (value is string strBool)
            {
                if (bool.TryParse(strBool, out var result))
                    return result;

                // Handle common variations
                var lower = strBool.ToLower();
                return lower == "true" || lower == "yes" || lower == "1" || lower == "on";
            }
            if (value is int intBool) return intBool != 0;
            return false;
        }

        // Handle number conversions
        if (expectedType == "integer" || expectedType == "int" || expectedType == "number")
        {
            if (value is int) return value;
            if (value is long) return value;
            if (value is double dbl) return (int)dbl;
            if (value is string strNum)
            {
                if (int.TryParse(strNum, out var intResult))
                    return intResult;
                if (double.TryParse(strNum, out var dblResult))
                    return (int)dblResult;
            }
            return value;
        }

        // Handle string conversions
        if (expectedType == "string")
        {
            return value.ToString();
        }

        // Return as-is for unknown types
        return value;
    }

    /// <summary>
    /// Finds a fuzzy match for a parameter name
    /// </summary>
    private static string? FindFuzzyMatch(string input, IEnumerable<string> candidates)
    {
        var normalizedInput = NormalizeParamName(input);

        foreach (var candidate in candidates)
        {
            var normalizedCandidate = NormalizeParamName(candidate);

            // Check if one contains the other
            if (normalizedInput.Contains(normalizedCandidate) ||
                normalizedCandidate.Contains(normalizedInput))
            {
                return candidate;
            }

            // Check Levenshtein distance for close matches
            if (GetLevenshteinDistance(normalizedInput, normalizedCandidate) <= 2)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes parameter names for comparison
    /// </summary>
    private static string NormalizeParamName(string name)
    {
        // Remove underscores, hyphens, and make lowercase
        return name.Replace("_", "").Replace("-", "").ToLower();
    }

    /// <summary>
    /// Calculates the Levenshtein distance between two strings
    /// </summary>
    private static int GetLevenshteinDistance(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1))
            return string.IsNullOrEmpty(s2) ? 0 : s2.Length;

        if (string.IsNullOrEmpty(s2))
            return s1.Length;

        int[,] distance = new int[s1.Length + 1, s2.Length + 1];

        for (int i = 0; i <= s1.Length; i++)
            distance[i, 0] = i;

        for (int j = 0; j <= s2.Length; j++)
            distance[0, j] = j;

        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[s1.Length, s2.Length];
    }
}