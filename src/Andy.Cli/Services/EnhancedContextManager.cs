using Andy.Model.Model;

namespace Andy.Cli.Services;

/// <summary>
/// Enhanced context entry with better tool call tracking
/// </summary>
public class EnhancedContextEntry
{
    public Role Role { get; set; }
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public int TokenEstimate { get; set; }
    public string? ToolId { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolResult { get; set; }
    public List<TrackedToolCall>? ToolCalls { get; set; }

    public EnhancedContextEntry Clone()
    {
        return new EnhancedContextEntry
        {
            Role = Role,
            Content = Content,
            Timestamp = Timestamp,
            TokenEstimate = TokenEstimate,
            ToolId = ToolId,
            ToolCallId = ToolCallId,
            ToolResult = ToolResult,
            ToolCalls = ToolCalls?.Select(tc => tc.Clone()).ToList()
        };
    }
}

/// <summary>
/// Tracked tool call with proper ID management
/// </summary>
public class TrackedToolCall
{
    public string CallId { get; set; } = "";
    public string ToolId { get; set; } = "";
    public Dictionary<string, object?> Parameters { get; set; } = new();

    public TrackedToolCall Clone()
    {
        return new TrackedToolCall
        {
            CallId = CallId,
            ToolId = ToolId,
            Parameters = new Dictionary<string, object?>(Parameters)
        };
    }
}