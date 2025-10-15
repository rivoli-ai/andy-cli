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

    public static ToolExecutionTracker Instance => _instance ??= new ToolExecutionTracker();

    public void SetFeedView(FeedView feedView)
    {
        _feedView = feedView;
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

        // If we have a FeedView, update it with the real parameters
        if (_feedView != null && parameters != null)
        {
            // Find any running tool with a matching base name and update its parameters
            // This is a workaround since SimpleAgent doesn't give us the actual parameters
            _feedView.UpdateRunningToolParameters(toolName, parameters);
        }
    }

    public void TrackToolComplete(string toolId, bool success, string? result)
    {
        if (_executions.TryGetValue(toolId, out var info))
        {
            info.EndTime = DateTime.UtcNow;
            info.Success = success;
            info.Result = result;
        }
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
    }
}