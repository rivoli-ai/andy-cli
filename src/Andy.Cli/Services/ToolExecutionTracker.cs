using System;
using System.Collections.Generic;
using Andy.Cli.Widgets;

namespace Andy.Cli.Services;

/// <summary>
/// Singleton service to track tool executions and share information between ToolAdapter and UI
/// </summary>
public class ToolExecutionTracker
{
    private static ToolExecutionTracker? _instance;
    private readonly Dictionary<string, ToolExecutionInfo> _executions = new();
    private readonly Dictionary<string, Dictionary<string, object?>> _pendingParameters = new(); // Store parameters immediately
    private readonly Dictionary<string, string> _toolNameToIdMap = new(); // Map tool names to their UI IDs
    private readonly Dictionary<string, string> _correlationIdToUiIdMap = new(); // Map correlation IDs to UI IDs
    private readonly Dictionary<string, Queue<string>> _pendingToolExecutions = new(); // Queue of UI IDs per tool name
    private readonly object _pendingToolsLock = new();
    private EnhancedFeedView? _feedView;
    private string? _lastActiveToolId; // Track the last tool started from UI
    private int _parameterUpdateCounter = 0;

    public static ToolExecutionTracker Instance => _instance ??= new ToolExecutionTracker();

    public void SetFeedView(EnhancedFeedView feedView)
    {
        _feedView = feedView;
    }

    public void SetLastActiveToolId(string toolId)
    {
        _lastActiveToolId = toolId;
    }

    public string? GetLastActiveToolId()
    {
        return _lastActiveToolId;
    }

    public void RegisterToolMapping(string toolName, string uiToolId)
    {
        _toolNameToIdMap[toolName.ToLower()] = uiToolId;
    }

    public void RegisterCorrelationMapping(string correlationId, string uiToolId)
    {
        if (!string.IsNullOrEmpty(correlationId))
        {
            _correlationIdToUiIdMap[correlationId] = uiToolId;
        }
    }

    public string? GetToolIdForCorrelation(string? correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
            return null;
        return _correlationIdToUiIdMap.TryGetValue(correlationId, out var id) ? id : null;
    }

    public string? GetToolIdForName(string toolName)
    {
        return _toolNameToIdMap.TryGetValue(toolName.ToLower(), out var id) ? id : null;
    }

    /// <summary>
    /// Enqueue a pending tool execution (called when ToolCalled event fires)
    /// </summary>
    public void EnqueuePendingTool(string toolName, string uiToolId)
    {
        lock (_pendingToolsLock)
        {
            var key = toolName.ToLower();
            if (!_pendingToolExecutions.ContainsKey(key))
            {
                _pendingToolExecutions[key] = new Queue<string>();
            }
            _pendingToolExecutions[key].Enqueue(uiToolId);
        }
    }

    /// <summary>
    /// Dequeue the next pending tool execution for a tool name (called when tool actually executes)
    /// </summary>
    public string? DequeuePendingTool(string toolName)
    {
        lock (_pendingToolsLock)
        {
            var key = toolName.ToLower();
            if (_pendingToolExecutions.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }
            return null;
        }
    }

    public EnhancedFeedView? GetFeedView()
    {
        return _feedView;
    }

    /// <summary>
    /// Store parameters immediately when we get them, even before tool execution
    /// </summary>
    public void StoreParameters(string toolName, Dictionary<string, object?> parameters)
    {
        lock (_pendingParameters)
        {
            var key = toolName.ToLower();
            _pendingParameters[key] = parameters;
            _parameterUpdateCounter++;

            // Also immediately update any running tools with matching names
            if (_feedView != null)
            {
                // Force immediate update of all matching tools
                _feedView.UpdateRunningToolParameters(toolName, parameters);
                _feedView.ForceUpdateAllMatchingTools(toolName, toolName, parameters);

                // If we have a lastActiveToolId, update that specific tool
                if (!string.IsNullOrEmpty(_lastActiveToolId))
                {
                    _feedView.UpdateToolByExactId(_lastActiveToolId, parameters);
                }
            }
        }
    }

    /// <summary>
    /// Get stored parameters for a tool
    /// </summary>
    public Dictionary<string, object?>? GetStoredParameters(string toolName)
    {
        lock (_pendingParameters)
        {
            var key = toolName.ToLower();
            var found = _pendingParameters.TryGetValue(key, out var parameters);
            // Don't use console output - it messes up the TUI
            return found ? parameters : null;
        }
    }

    public void TrackToolStart(string toolId, string toolName, Dictionary<string, object?>? parameters)
    {
        var info = new ToolExecutionInfo
        {
            ToolId = toolId,
            ToolName = toolName,
            Parameters = parameters,
            StartTime = DateTime.UtcNow
        };

        _executions[toolId] = info;

        // Also store with the last active tool ID if it's set (to link UI tool with actual execution)
        if (!string.IsNullOrEmpty(_lastActiveToolId))
        {
            _executions[_lastActiveToolId] = info;
        }

        // NOTE: UI parameter update is now handled by UiUpdatingToolExecutor using exact tool ID
        // This ensures correct parameter display for parallel tool executions
    }

    public ToolExecutionInfo? GetExecutionInfo(string toolId)
    {
        // Try exact match first
        if (_executions.TryGetValue(toolId, out var info))
            return info;

        // Try to find by base tool name (in case the toolId has a counter suffix)
        var baseToolId = toolId.Contains('_') ?
            toolId.Substring(0, toolId.LastIndexOf('_')) :
            toolId;

        // Find the most recent execution with matching base name
        foreach (var kvp in _executions.Reverse())
        {
            var execBaseId = kvp.Key.Contains('_') ?
                kvp.Key.Substring(0, kvp.Key.LastIndexOf('_')) :
                kvp.Key;

            if (execBaseId.Equals(baseToolId, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        return null;
    }

    public void TrackToolComplete(string toolId, bool success, string? result, object? resultData = null)
    {
        if (_executions.TryGetValue(toolId, out var info))
        {
            info.EndTime = DateTime.UtcNow;
            info.Success = success;
            info.Result = result;
            info.ResultData = resultData;

            // Only format a result summary for successful operations
            // For failures, preserve the detailed error message that was extracted
            if (success)
            {
                var resultSummary = FormatResultSummary(info.ToolName, info.Parameters, resultData, result);
                if (!string.IsNullOrEmpty(resultSummary))
                {
                    info.Result = resultSummary;
                }
            }

            // For datetime_tool, capture the actual result
            if (info.ToolName.Contains("datetime", StringComparison.OrdinalIgnoreCase))
            {
                // The actual date/time is typically in the result string
                // But don't add "Output:" prefix if it's an error
                if (!string.IsNullOrEmpty(result) && success)
                {
                    info.Result = result; // Just use the result directly
                }
                else if (!string.IsNullOrEmpty(result))
                {
                    info.Result = result; // For errors, also use directly
                }
            }

            // If we have a FeedView, update it with detailed result
            if (_feedView != null && info.Parameters != null)
            {
                _feedView.UpdateToolResult(toolId, info.ToolName, success, resultData, info.Parameters);
            }
        }
    }

    private string? FormatResultSummary(string toolName, Dictionary<string, object?>? parameters, object? resultData, string? fallbackResult)
    {
        // For read_file, show what file was read
        if (toolName.Contains("read_file", StringComparison.OrdinalIgnoreCase))
        {
            if (parameters?.TryGetValue("file_path", out var filePath) == true && filePath != null)
            {
                var fileName = System.IO.Path.GetFileName(filePath.ToString() ?? "");

                // Try to get line count from result data
                if (resultData is Dictionary<string, object?> resultDict)
                {
                    if (resultDict.TryGetValue("metadata", out var metadata) && metadata is Dictionary<string, object?> metaDict)
                    {
                        if (metaDict.TryGetValue("line_count", out var lines))
                            return $"Read {fileName} ({lines} lines)";
                    }
                }

                return $"Read {fileName}";
            }
        }
        // For list_directory, show directory name
        else if (toolName.Contains("list_directory", StringComparison.OrdinalIgnoreCase))
        {
            if (parameters?.TryGetValue("path", out var path) == true && path != null)
            {
                var dirName = path.ToString() ?? ".";
                if (dirName == ".") dirName = "current directory";

                // Try to get counts from result
                if (resultData is Dictionary<string, object?> resultDict &&
                    resultDict.TryGetValue("entries", out var entries) && entries is System.Collections.IEnumerable entryList)
                {
                    var count = 0;
                    foreach (var _ in entryList) count++;
                    return $"Listed {dirName} ({count} items)";
                }

                return $"Listed {dirName}";
            }
        }
        // For write_file, show what file was written
        else if (toolName.Contains("write_file", StringComparison.OrdinalIgnoreCase))
        {
            if (parameters?.TryGetValue("file_path", out var filePath) == true && filePath != null)
            {
                var fileName = System.IO.Path.GetFileName(filePath.ToString() ?? "");
                return $"Wrote {fileName}";
            }
        }

        return fallbackResult;
    }

    public class ToolExecutionInfo
    {
        public string ToolId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public Dictionary<string, object?>? Parameters { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public bool Success { get; set; }
        public string? Result { get; set; }
        public object? ResultData { get; set; }
    }
}