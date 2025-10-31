namespace Andy.Cli.Services;

/// <summary>
/// Defines output size limits for different tools to prevent context overflow
/// </summary>
public static class ToolOutputLimits
{
    /// <summary>
    /// Maximum characters for tool outputs sent to LLM context
    /// </summary>
    public static readonly Dictionary<string, int> Limits = new()
    {
        // File operations - very conservative for multiple calls
        ["read_file"] = 1500,  // Further reduced for multiple file reads
        ["search_files"] = 1000, // Aggressive limit
        ["search_text"] = 1000,  // Aggressive limit
        ["list_directory"] = 800, // Very aggressive - directories cause most issues
        ["code_index"] = 1200,  // Reduced
        
        // Command execution - prevent huge outputs
        ["bash"] = 1000,  // Reduced
        ["bash_command"] = 1000,  // Reduced
        ["execute_command"] = 1000, // Reduced
        
        // Web operations - limit response sizes
        ["http_request"] = 1500,  // Reduced
        ["web_search"] = 1000,  // Reduced
        
        // Default for any unspecified tool
        ["_default"] = 1000  // Very conservative default
    };
    
    /// <summary>
    /// Get the output limit for a specific tool
    /// </summary>
    public static int GetLimit(string toolId)
    {
        // Normalize tool ID (remove any prefixes/suffixes)
        var normalizedId = toolId.ToLowerInvariant().Replace("-", "_").Replace(".", "_");
        
        // Check for exact match
        if (Limits.TryGetValue(normalizedId, out var limit))
        {
            return limit;
        }
        
        // Check for partial matches (e.g., "read_file_tool" matches "read_file")
        foreach (var kvp in Limits)
        {
            if (kvp.Key != "_default" && normalizedId.Contains(kvp.Key))
            {
                return kvp.Value;
            }
        }
        
        // Return default
        return Limits["_default"];
    }
    
    /// <summary>
    /// Apply output limit to a tool result
    /// </summary>
    public static string LimitOutput(string toolId, string output, int? overrideLimit = null)
    {
        if (string.IsNullOrEmpty(output))
        {
            return output;
        }
        
        var limit = overrideLimit ?? GetLimit(toolId);
        
        // Log when we're truncating (helps debug)
        if (output.Length > limit)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolOutputLimits] Truncating {toolId} output from {output.Length} to ~{limit} chars");
        }
        
        if (output.Length <= limit)
        {
            return output;
        }
        
        // Truncate with informative message
        var truncated = output.Substring(0, limit);
        var remainingChars = output.Length - limit;
        
        // Try to truncate at a natural boundary (newline or space)
        var lastNewline = truncated.LastIndexOf('\n');
        var lastSpace = truncated.LastIndexOf(' ');
        
        if (lastNewline > 0 && lastNewline > limit - 200) // If there's a newline near the end
        {
            truncated = output.Substring(0, lastNewline);
            remainingChars = output.Length - lastNewline;
        }
        else if (lastSpace > 0 && lastSpace > limit - 50) // If there's a space near the end
        {
            truncated = output.Substring(0, lastSpace);
            remainingChars = output.Length - lastSpace;
        }
        
        // Add truncation notice
        return truncated + $"\n\n[Output truncated - {remainingChars:N0} characters omitted. Tool: {toolId}]";
    }
}