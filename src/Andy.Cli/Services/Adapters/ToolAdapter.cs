using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.Adapters;

/// <summary>
/// Adapts Andy.Tools to Andy.Model.Tooling.ITool interface
/// </summary>
public class ToolAdapter : Andy.Model.Tooling.ITool
{
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly string _toolId;
    private readonly ILogger<ToolAdapter>? _logger;

    public Andy.Model.Tooling.ToolDeclaration Definition { get; }

    public ToolAdapter(string toolId, IToolRegistry toolRegistry, IToolExecutor toolExecutor, ILogger<ToolAdapter>? logger = null)
    {
        _toolId = toolId;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _logger = logger;

        // Initialize Definition from tool metadata
        var tool = _toolRegistry.GetTool(toolId);
        if (tool != null)
        {
            var parameters = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = ConvertParametersToSchema(tool.Metadata.Parameters),
                ["required"] = tool.Metadata.Parameters
                    .Where(p => p.Required)
                    .Select(p => p.Name)
                    .ToArray()
            };

            Definition = new Andy.Model.Tooling.ToolDeclaration
            {
                Name = tool.Metadata.Id,
                Description = tool.Metadata.Description,
                Parameters = parameters
            };
        }
        else
        {
            Definition = new Andy.Model.Tooling.ToolDeclaration
            {
                Name = toolId,
                Description = "Tool",
                Parameters = new Dictionary<string, object>()
            };
        }
    }

    private Dictionary<string, object> ConvertParametersToSchema(IList<ToolParameter> parameters)
    {
        var properties = new Dictionary<string, object>();

        foreach (var param in parameters)
        {
            var propSchema = new Dictionary<string, object>
            {
                ["type"] = ConvertTypeToJsonSchema(param.Type ?? "string"),
                ["description"] = param.Description
            };

            // If it's an array type, add items schema
            if (param.Type?.ToLowerInvariant() is "array" or "list")
            {
                propSchema["items"] = new Dictionary<string, object>
                {
                    ["type"] = "string" // Default to string items, could be enhanced later
                };
            }

            // Add enum values if specified
            if (param.AllowedValues != null && param.AllowedValues.Any())
            {
                propSchema["enum"] = param.AllowedValues.ToArray();
            }

            if (param.DefaultValue != null)
            {
                propSchema["default"] = param.DefaultValue;
            }

            properties[param.Name] = propSchema;
        }

        return properties;
    }

    private string ConvertTypeToJsonSchema(string dotNetType)
    {
        return dotNetType?.ToLowerInvariant() switch
        {
            "string" => "string",
            "int" or "int32" or "integer" => "integer",
            "long" or "int64" => "integer",
            "bool" or "boolean" => "boolean",
            "double" or "float" or "decimal" => "number",
            "array" or "list" => "array",
            "dictionary" or "object" => "object",
            _ => "string"
        };
    }

    private object? ConvertJsonElement(object? value)
    {
        if (value is not JsonElement element)
            return value;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out var intVal) ? (object)intVal : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(e => ConvertJsonElement(e)).ToArray(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                prop => prop.Name,
                prop => ConvertJsonElement(prop.Value)
            ),
            _ => element.ToString()
        };
    }

    public async Task<Andy.Model.Model.ToolResult> ExecuteAsync(Andy.Model.Model.ToolCall call, CancellationToken ct = default)
    {
        try
        {
            // Parse arguments from JSON
            var rawParameters = string.IsNullOrEmpty(call.ArgumentsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(call.ArgumentsJson) ?? new Dictionary<string, object?>();

            // Convert JsonElement values to their actual types
            var parameters = new Dictionary<string, object?>();
            foreach (var kvp in rawParameters)
            {
                parameters[kvp.Key] = ConvertJsonElement(kvp.Value);
            }

            // IMMEDIATELY store parameters so UI can access them
            ToolExecutionTracker.Instance.StoreParameters(call.Name, parameters);
            ToolExecutionTracker.Instance.StoreParameters(_toolId, parameters);

            // Track and log the actual parameters - use call.Id for unique tracking
            ToolExecutionTracker.Instance.TrackToolStart(call.Id, call.Name, parameters);
            // Also track with the base tool ID for backward compatibility
            ToolExecutionTracker.Instance.TrackToolStart(_toolId, call.Name, parameters);

            // Log what we received for debugging with more detail
            _logger?.LogInformation("[TOOL_EXEC_START] Tool: {ToolId}, CallName: {CallName}, CallId: {CallId}, ParamCount: {ParamCount}",
                _toolId, call.Name, call.Id, parameters.Count);
            foreach (var param in parameters.Take(5)) // Log first 5 parameters
            {
                var value = param.Value?.ToString() ?? "null";
                if (value.Length > 100) value = value.Substring(0, 97) + "...";
                _logger?.LogInformation("[TOOL_PARAM] {Key}: {Value}", param.Key, value);
            }
            if (parameters.Count > 5)
            {
                _logger?.LogInformation("[TOOL_PARAM] ... and {Count} more parameters", parameters.Count - 5);
            }

            // Execute using Andy.Tools
            var context = new Andy.Tools.Core.ToolExecutionContext
            {
                CancellationToken = ct
            };

            var startTime = DateTime.UtcNow;
            var result = await _toolExecutor.ExecuteAsync(_toolId, parameters, context);
            var duration = DateTime.UtcNow - startTime;

            _logger?.LogInformation("[TOOL_EXEC_END] Tool: {ToolId}, Duration: {Duration}ms, Success: {Success}",
                _toolId, duration.TotalMilliseconds, result.IsSuccessful);

            // Track completion with full result data - use call.Id for unique tracking
            ToolExecutionTracker.Instance.TrackToolComplete(call.Id, result.IsSuccessful, result.Message, result.Data);
            // Also track with the base tool ID for backward compatibility
            ToolExecutionTracker.Instance.TrackToolComplete(_toolId, result.IsSuccessful, result.Message, result.Data);

            // Convert to Andy.Model.ToolResult
            if (result.IsSuccessful)
            {
                // Log success details
                if (result.Data != null)
                {
                    var dataStr = JsonSerializer.Serialize(result.Data);
                    if (_toolId.Contains("read_file") && dataStr.Length > 500)
                    {
                        // For file reads, log line count and truncated preview
                        var lines = dataStr.Split('\n').Length;
                        _logger?.LogInformation("[TOOL_RESULT] Read {Lines} lines from file", lines);
                        _logger?.LogDebug("[TOOL_PREVIEW] {Preview}...", dataStr.Substring(0, 500));
                    }
                    else if (_toolId.Contains("update") || _toolId.Contains("edit"))
                    {
                        // For updates, try to extract diff statistics
                        _logger?.LogInformation("[TOOL_RESULT] File updated successfully");
                        if (dataStr.Contains("+") || dataStr.Contains("-"))
                        {
                            var additions = dataStr.Count(c => c == '+');
                            var deletions = dataStr.Count(c => c == '-');
                            _logger?.LogInformation("[TOOL_STATS] {Additions} additions, {Deletions} deletions", additions, deletions);
                        }
                    }
                    else if (dataStr.Length > 200)
                    {
                        _logger?.LogInformation("[TOOL_RESULT] {Preview}...", dataStr.Substring(0, 200));
                    }
                    else
                    {
                        _logger?.LogInformation("[TOOL_RESULT] {Result}", dataStr);
                    }
                }

                // Try to use Data if available, otherwise use Message
                object? resultData = result.Data;

                 if (resultData == null && !string.IsNullOrEmpty(result.Message))
                {
                    resultData = new { message = result.Message };
                }

                return Andy.Model.Model.ToolResult.FromObject(call.Id, call.Name, resultData ?? new { }, isError: false);
            }
            else
            {
                // Log error details
                _logger?.LogWarning("[TOOL_ERROR] Tool {ToolId} failed: {Error}",
                    _toolId, result.ErrorMessage ?? result.Message ?? "Unknown error");

                // Return error result
                var errorData = new
                {
                    error = result.ErrorMessage ?? result.Message ?? "Tool execution failed",
                    details = result.Data
                };
                return Andy.Model.Model.ToolResult.FromObject(call.Id, call.Name, errorData, isError: true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error executing tool {ToolId}", _toolId);
            return Andy.Model.Model.ToolResult.FromObject(call.Id, call.Name,
                new { error = ex.Message, type = ex.GetType().Name }, isError: true);
        }
    }
}

/// <summary>
/// Factory that creates a ToolRegistry populated with adapters for Andy.Tools
/// </summary>
public class ToolRegistryAdapter
{
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly ILogger<ToolRegistryAdapter>? _logger;
    private readonly Andy.Model.Tooling.ToolRegistry _modelToolRegistry;
    private readonly string? _providerName;

    // Essential tools for Cerebras (limited to 4 to avoid 400 errors)
    private static readonly HashSet<string> CerebrasEssentialTools = new()
    {
        "list_directory",
        "read_file",
        "bash_command",
        "search_files"
    };

    public ToolRegistryAdapter(IToolRegistry toolRegistry, IToolExecutor toolExecutor, ILogger<ToolRegistryAdapter>? logger = null, string? providerName = null)
    {
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _logger = logger;
        _providerName = providerName;
        _modelToolRegistry = new Andy.Model.Tooling.ToolRegistry();
        InitializeTools();
    }

    public Andy.Model.Tooling.ToolRegistry GetToolRegistry() => _modelToolRegistry;

    private void InitializeTools()
    {
        // Get all enabled tools and create adapters for them
        var enabledTools = _toolRegistry.GetTools(enabledOnly: true);

        // For Cerebras, limit to essential tools to avoid 400 Bad Request errors
        if (_providerName?.Contains("cerebras", StringComparison.OrdinalIgnoreCase) == true)
        {
            enabledTools = enabledTools.Where(t => CerebrasEssentialTools.Contains(t.Metadata.Id)).ToList();
            _logger?.LogInformation("Limiting tools for Cerebras provider to: {Tools}", string.Join(", ", enabledTools.Select(t => t.Metadata.Id)));
        }

        foreach (var tool in enabledTools)
        {
            var adapter = new ToolAdapter(tool.Metadata.Id, _toolRegistry, _toolExecutor);
            _modelToolRegistry.Register(adapter);
        }
    }
}