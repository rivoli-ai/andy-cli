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
    private FeedView? _feedView;
    private string? _lastActiveToolId; // Track the last tool started from UI

    public static ToolExecutionTracker Instance => _instance ??= new ToolExecutionTracker();

    public void SetFeedView(FeedView feedView)
    {
        _feedView = feedView;
    }

    public void SetLastActiveToolId(string toolId)
    {
        _lastActiveToolId = toolId;
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

        // Always update the UI with parameters - try multiple approaches
        if (_feedView != null && parameters != null)
        {
            // First, try to update by exact ID if we have it
            if (!string.IsNullOrEmpty(_lastActiveToolId))
            {
                _feedView.UpdateToolByExactId(_lastActiveToolId, parameters);
            }

            // Also update by tool name
            _feedView.UpdateRunningToolParameters(toolName, parameters);

            // Try to update any active tool with the base tool ID
            _feedView.UpdateActiveToolWithParameters(toolId, parameters);

            // Force update all matching tools
            _feedView.ForceUpdateAllMatchingTools(toolId, toolName, parameters);
        }
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

            // Format a meaningful result summary based on tool type and data
            var resultSummary = FormatResultSummary(info.ToolName, info.Parameters, resultData, result);
            if (!string.IsNullOrEmpty(resultSummary))
            {
                info.Result = resultSummary;
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