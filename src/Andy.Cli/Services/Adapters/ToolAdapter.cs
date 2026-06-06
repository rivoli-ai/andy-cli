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

    /// <summary>
    /// Identifies the shell execution tool regardless of which alias the model used.
    /// </summary>
    private static bool IsExecuteCommandTool(string toolId, string callName)
    {
        return string.Equals(toolId, "execute_command", StringComparison.OrdinalIgnoreCase)
            || string.Equals(callName, "execute_command", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a human-readable reason if the supplied execute_command parameters describe a command that is
    /// missing or degenerate (no runnable executable token), otherwise null. A "degenerate" command is one
    /// that, after trimming, is empty or whose first non-assignment token is not a usable executable name
    /// (for example a bare number such as "1", a flag, or a pure shell metacharacter). Such inputs are
    /// symptoms of a mangled tool call and must never reach the permission prompt or the shell.
    /// </summary>
    internal static string? GetInvalidCommandReason(IReadOnlyDictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("command", out var commandObj) || commandObj == null)
        {
            return "execute_command was called without a 'command' argument. Provide the full shell command to run.";
        }

        var command = commandObj.ToString() ?? string.Empty;
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
        {
            return "execute_command was called with an empty 'command'. Provide the full shell command to run.";
        }

        if (!HasRunnableExecutable(trimmed))
        {
            return $"execute_command received a malformed command ('{command}') with no runnable executable. "
                 + "This usually means the command was garbled (for example a numbered-list prefix). "
                 + "Re-issue the call with the complete command, such as 'dotnet test'.";
        }

        return null;
    }

    /// <summary>
    /// Determines whether a command string begins with something that could plausibly be an executable: a
    /// bare word that is not a number, not a flag, and not made of shell metacharacters. Leading
    /// <c>VAR=value</c> environment assignments are skipped first.
    /// </summary>
    private static bool HasRunnableExecutable(string command)
    {
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;

        // Skip leading environment assignments (FOO=bar) that precede the executable.
        while (i < tokens.Length && IsEnvAssignment(tokens[i]))
        {
            i++;
        }

        if (i >= tokens.Length)
        {
            return false;
        }

        var token = tokens[i];

        // A flag/option cannot be the executable.
        if (token.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        // A purely numeric token (e.g. "1" from a markdown list) is not a command.
        if (IsAllDigits(token))
        {
            return false;
        }

        // Must contain at least one character usable in an executable name.
        foreach (var c in token)
        {
            if (char.IsLetterOrDigit(c) || c == '/' || c == '\\' || c == '.' || c == '_' || c == '-')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEnvAssignment(string token)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        for (int j = 0; j < eq; j++)
        {
            var c = token[j];
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllDigits(string token)
    {
        foreach (var c in token)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return token.Length > 0;
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

            // Reject degenerate shell invocations BEFORE prompting the user or executing anything.
            // The upstream LLM/function-call parser can occasionally emit a mangled execute_command whose
            // "command" argument is empty or a bare token with no executable (for example the literal "1"
            // taken from a markdown numbered list such as "1. dotnet build"). Passing that straight through
            // would (a) show a nonsensical permission prompt for the command "1" and (b) run garbage in the
            // shell, where the OS can react in surprising ways. Fail fast with a clear message instead.
            if (IsExecuteCommandTool(_toolId, call.Name))
            {
                var invalidReason = GetInvalidCommandReason(parameters);
                if (invalidReason != null)
                {
                    _logger?.LogWarning("[TOOL_ADAPTER] Rejecting malformed execute_command call: {Reason}", invalidReason);
                    return Andy.Model.Model.ToolResult.FromObject(
                        call.Id, call.Name,
                        new { error = invalidReason },
                        isError: true);
                }
            }

            // Log what we're about to do
            _logger?.LogWarning("[TOOL_ADAPTER] Executing {ToolName} with {ParamCount} parameters: {Params}",
                call.Name, parameters.Count, string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}")));

            // DEBUG: Write to file
            try
            {
                var debugInfo = $"[{DateTime.Now:HH:mm:ss.fff}] ToolAdapter.ExecuteAsync:\n";
                debugInfo += $"  call.Name: {call.Name}\n";
                debugInfo += $"  _toolId: {_toolId}\n";
                debugInfo += $"  Parameters: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}\n";
                System.IO.File.AppendAllText("/tmp/tool_adapter_debug.txt", debugInfo);
            }
            catch { }

            // IMMEDIATELY update the UI with actual parameters
            // The UI should already exist from the ToolCalled event
            var feedView = ToolExecutionTracker.Instance.GetFeedView();
            _logger?.LogWarning("[TOOL_ADAPTER] FeedView is {State}", feedView != null ? "available" : "NULL");

            if (feedView != null)
            {
                // Try to find the UI tool ID for this tool by name - try multiple variations
                var toolId = ToolExecutionTracker.Instance.GetToolIdForName(call.Name) ??
                             ToolExecutionTracker.Instance.GetToolIdForName(_toolId) ??
                             ToolExecutionTracker.Instance.GetToolIdForName(call.Name.Replace("-", "_")) ??
                             ToolExecutionTracker.Instance.GetToolIdForName(call.Name.Replace("_", "-"));

                _logger?.LogWarning("[TOOL_ADAPTER] Found UI toolId for {ToolName}: {ToolId}", call.Name, toolId ?? "NULL");

                // DEBUG: Write lookup result
                try
                {
                    var debugInfo = $"  Lookup result: toolId = {toolId ?? "NULL"}\n";
                    debugInfo += $"  About to update: {!string.IsNullOrEmpty(toolId)}\n\n";
                    System.IO.File.AppendAllText("/tmp/tool_adapter_debug.txt", debugInfo);
                }
                catch { }

                if (!string.IsNullOrEmpty(toolId))
                {
                    _logger?.LogWarning("[TOOL_ADAPTER] Updating UI for {ToolId} with {ParamCount} parameters",
                        toolId, parameters.Count);

                    // Update the existing UI entry with real parameters
                    feedView.UpdateToolByExactId(toolId, parameters);
                    feedView.ForceUpdateAllMatchingTools(toolId, call.Name, parameters);
                }
                else
                {
                    _logger?.LogError("[TOOL_ADAPTER] No UI tool ID found for {ToolName}/{ToolId} - cannot update UI!", call.Name, _toolId);
                }
            }
            else
            {
                _logger?.LogError("[TOOL_ADAPTER] FeedView not available - cannot update UI!");
            }

            // Store parameters for later use
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
        "execute_command",
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