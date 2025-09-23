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
                ["type"] = ConvertTypeToJsonSchema(param.Type),
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

            // Log what we received for debugging
            _logger?.LogDebug("Tool {ToolId} called with arguments: {Arguments}", _toolId, call.ArgumentsJson);
            _logger?.LogDebug("Parsed parameters: {Parameters}", JsonSerializer.Serialize(parameters));

            // Execute using Andy.Tools
            var context = new Andy.Tools.Core.ToolExecutionContext
            {
                CancellationToken = ct
            };
            var result = await _toolExecutor.ExecuteAsync(_toolId, parameters, context);

            // Convert to Andy.Model.ToolResult
            if (result.IsSuccessful)
            {
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