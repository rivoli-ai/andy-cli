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
            ["location"] = "path",
            ["directory_path"] = "path"
        },
        ["create_directory"] = new Dictionary<string, string>
        {
            ["directory"] = "path",
            ["dir"] = "path",
            ["folder"] = "path",
            ["name"] = "path"
        },
        // The Replace Text tool's parameters are search_pattern / replacement_text / target_path,
        // but models routinely call it with the names from their own file-edit tools
        // (old_string/new_string, find/replace, etc.). Without these aliases the required
        // parameters arrive null, the permission prompt shows "Replace None ''", and the edit fails.
        ["replace_text"] = new Dictionary<string, string>
        {
            ["old_string"] = "search_pattern",
            ["old_str"] = "search_pattern",
            ["old_text"] = "search_pattern",
            ["old"] = "search_pattern",
            ["find"] = "search_pattern",
            ["search"] = "search_pattern",
            ["search_string"] = "search_pattern",
            ["search_text"] = "search_pattern",
            ["pattern"] = "search_pattern",
            ["query"] = "search_pattern",
            ["new_string"] = "replacement_text",
            ["new_str"] = "replacement_text",
            ["new_text"] = "replacement_text",
            ["new"] = "replacement_text",
            ["replace"] = "replacement_text",
            ["replacement"] = "replacement_text",
            ["replace_with"] = "replacement_text",
            ["replacement_string"] = "replacement_text",
            ["replace_text"] = "replacement_text",
            ["file_path"] = "target_path",
            ["path"] = "target_path",
            ["filepath"] = "target_path",
            ["filename"] = "target_path",
            ["file"] = "target_path",
            ["target_file"] = "target_path"
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
    /// Coerces parameter VALUES to the types declared in the tool metadata, without renaming
    /// parameters or fuzzy-matching names. This is the safe subset of <see cref="MapParameters"/>:
    /// it only touches values for parameters whose names already match the tool's metadata.
    ///
    /// The motivating case is the common LLM mistake of passing an array-typed parameter as a
    /// bare scalar (e.g. file_patterns="*.cs" instead of ["*.cs"]). The tool framework's
    /// validator rejects that with PARAMETER_TYPE_MISMATCH ("must be an array") before the tool
    /// ever runs, so the call fails for a reason that has nothing to do with the search itself.
    /// </summary>
    public static Dictionary<string, object?> NormalizeParameterTypes(
        Dictionary<string, object?> inputParameters,
        ToolMetadata toolMetadata)
    {
        var paramMetadata = toolMetadata.Parameters.ToDictionary(p => p.Name.ToLower(), p => p);
        var result = new Dictionary<string, object?>(inputParameters.Count);
        foreach (var kvp in inputParameters)
        {
            var value = kvp.Value;
            if (value != null && paramMetadata.TryGetValue(kvp.Key.ToLower(), out var metadata))
            {
                value = ConvertParameterType(value, metadata);
            }
            result[kvp.Key] = value;
        }
        return result;
    }

    /// <summary>
    /// Renames input parameters using ONLY exact name matches and the curated, per-tool alias
    /// table (<see cref="ToolParameterMappings"/>), then coerces values to the declared types.
    /// Unlike <see cref="MapParameters"/> this deliberately performs NO fuzzy/Levenshtein matching,
    /// so it cannot mis-route a call to the wrong parameter - every rename is hand-vetted. This is
    /// the live-dispatch path: it lets the model use familiar names (e.g. old_string/new_string for
    /// replace_text) while staying as safe as the value-only <see cref="NormalizeParameterTypes"/>.
    /// </summary>
    public static Dictionary<string, object?> MapAndNormalize(
        string toolId,
        Dictionary<string, object?> inputParameters,
        ToolMetadata toolMetadata)
    {
        var expectedParams = toolMetadata.Parameters.ToDictionary(p => p.Name.ToLower(), p => p.Name);
        var paramMetadata = toolMetadata.Parameters.ToDictionary(p => p.Name.ToLower(), p => p);
        var hasToolMapping = ToolParameterMappings.TryGetValue(toolId, out var mappings);

        var result = new Dictionary<string, object?>(inputParameters.Count);
        foreach (var kvp in inputParameters)
        {
            var name = kvp.Key;
            var value = kvp.Value;
            string finalName = name;

            if (expectedParams.TryGetValue(name.ToLower(), out var exact))
            {
                finalName = exact;
            }
            else if (hasToolMapping &&
                     mappings!.TryGetValue(name.ToLower(), out var mapped) &&
                     expectedParams.TryGetValue(mapped.ToLower(), out var mappedExact))
            {
                finalName = mappedExact;
            }
            // No fuzzy fallback: unrecognized names pass through unchanged.

            if (value != null && paramMetadata.TryGetValue(finalName.ToLower(), out var metadata))
            {
                value = ConvertParameterType(value, metadata);
            }

            // If a curated alias collides with a value already set (e.g. the model sent both the
            // canonical name and an alias), keep the first non-null value rather than clobbering.
            if (result.TryGetValue(finalName, out var existing) && existing != null && value == null)
                continue;
            result[finalName] = value;
        }
        return result;
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