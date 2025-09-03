using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Andy.Cli.Widgets;

namespace Andy.Cli.Services;

/// <summary>
/// Service for executing tools and managing their display in the TUI
/// </summary>
public class ToolExecutionService
{
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly FeedView _feed;
    private readonly int _maxDisplayLines;

    public ToolExecutionService(
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        int maxDisplayLines = 10)
    {
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _feed = feed;
        _maxDisplayLines = maxDisplayLines;
    }

    /// <summary>
    /// Execute a tool and display the results
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteToolAsync(
        string toolId,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        // Get the tool registration
        var toolReg = _toolRegistry.GetTool(toolId);
        if (toolReg == null)
        {
            return new ToolExecutionResult
            {
                IsSuccessful = false,
                ErrorMessage = $"Tool '{toolId}' not found",
                FullOutput = "",
                TruncatedForDisplay = false
            };
        }

        // Display tool execution start
        _feed.AddMarkdownRich($"### Executing Tool: {toolReg.Metadata.Name}");
        _feed.AddMarkdownRich($"**Tool ID:** `{toolId}`");
        
        // Display parameters
        if (parameters.Any())
        {
            var paramDisplay = new StringBuilder();
            paramDisplay.AppendLine("**Parameters:**");
            foreach (var param in parameters)
            {
                var value = param.Value?.ToString() ?? "null";
                // Truncate long values
                if (value.Length > 100)
                {
                    value = value.Substring(0, 97) + "...";
                }
                paramDisplay.AppendLine($"  - `{param.Key}`: {value}");
            }
            _feed.AddMarkdownRich(paramDisplay.ToString());
        }

        // Create execution context
        var context = new ToolExecutionContext
        {
            CancellationToken = cancellationToken
        };

        // Track output for display
        var outputLines = new List<string>();
        var fullOutput = new StringBuilder();
        var displayedLines = 0;
        var truncated = false;

        try
        {
            // Execute the tool
            var result = await _toolExecutor.ExecuteAsync(toolId, parameters, context);
            
            // Display completion status
            if (result.IsSuccessful)
            {
                _feed.AddMarkdownRich($"**Tool completed successfully**");
                
                // Display the output if available
                string output = "";
                
                // Handle different output types
                if (result.Data != null)
                {
                    // If Data is populated, serialize it as JSON
                    output = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions 
                    { 
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                }
                else if (result.Output != null)
                {
                    // Fallback to Output property
                    output = result.Output.ToString() ?? "";
                }
                
                if (!string.IsNullOrEmpty(output))
                {
                    fullOutput.Append(output);
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    outputLines.AddRange(lines);
                    
                    foreach (var line in lines)
                    {
                        if (displayedLines < _maxDisplayLines)
                        {
                            _feed.AddCode(line, "json");
                            displayedLines++;
                        }
                        else if (!truncated)
                        {
                            truncated = true;
                            _feed.AddMarkdownRich($"*... output truncated (showing first {_maxDisplayLines} lines) ...*");
                        }
                    }
                }
                
                // If output was truncated, show summary
                if (truncated)
                {
                    var totalLines = outputLines.Count;
                    _feed.AddMarkdownRich($"*Total output: {totalLines} lines, {fullOutput.Length} characters*");
                }
            }
            else
            {
                _feed.AddMarkdownRich($"**Tool execution failed:** {result.Error}");
            }

            // Create extended result with properly serialized output
            var extendedResult = new ToolExecutionResult
            {
                IsSuccessful = result.IsSuccessful,
                Data = result.Data,
                ErrorMessage = result.ErrorMessage,
                Metadata = result.Metadata,
                DurationMs = result.DurationMs,
                Message = result.Message,
                FullOutput = fullOutput.Length > 0 ? fullOutput.ToString() : (result.Message ?? ""),
                TruncatedForDisplay = truncated
            };
            
            return extendedResult;
        }
        catch (Exception ex)
        {
            _feed.AddMarkdownRich($"**Tool execution error:** {ex.Message}");
            
            return new ToolExecutionResult
            {
                IsSuccessful = false,
                ErrorMessage = ex.Message,
                FullOutput = fullOutput.ToString(),
                TruncatedForDisplay = false
            };
        }
    }

    /// <summary>
    /// Parse tool call from LLM response
    /// </summary>
    public static ToolCall? ParseToolCall(string llmResponse)
    {
        // This is a simplified parser - in production you'd want more robust parsing
        // Look for patterns like: <tool>toolname</tool> <params>...</params>
        // Or JSON format: {"tool": "toolname", "params": {...}}
        
        // For now, return null - we'll implement proper parsing later
        return null;
    }
}

/// <summary>
/// Extended tool execution result with full output tracking
/// </summary>
public class ToolExecutionResult : Andy.Tools.Core.ToolResult
{
    public string? FullOutput { get; set; }
    public bool TruncatedForDisplay { get; set; }
}

/// <summary>
/// Represents a tool call parsed from LLM response
/// </summary>
public class ToolCall
{
    public string ToolId { get; set; } = "";
    public Dictionary<string, object?> Parameters { get; set; } = new();
}