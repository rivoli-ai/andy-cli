using System.Text.Json;
using Andy.Model.Model;

namespace Andy.Cli.Instrumentation;

/// <summary>
/// Base class for all instrumentation events
/// </summary>
public abstract class InstrumentationEvent
{
    private static long _sequenceCounter = 0;

    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public long SequenceNumber { get; } = Interlocked.Increment(ref _sequenceCounter);
    public abstract string EventType { get; }
}

/// <summary>
/// Event fired when an LLM request is about to be sent
/// </summary>
public class LlmRequestEvent : InstrumentationEvent
{
    public override string EventType => "LlmRequest";

    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public int ConversationTurns { get; set; }
    public int EstimatedInputTokens { get; set; }
    public List<MessageSummary> ConversationHistory { get; set; } = new();
}

/// <summary>
/// Event fired when an LLM response is received
/// </summary>
public class LlmResponseEvent : InstrumentationEvent
{
    public override string EventType => "LlmResponse";

    public Guid RequestId { get; set; }
    public bool Success { get; set; }
    public string? StopReason { get; set; }
    public string? Response { get; set; }
    public int ResponseLength { get; set; }
    public int EstimatedOutputTokens { get; set; }
    public TimeSpan Duration { get; set; }
    public int? ActualInputTokens { get; set; }
    public int? ActualOutputTokens { get; set; }
}

/// <summary>
/// Event fired when a tool is about to be called
/// </summary>
public class ToolCallEvent : InstrumentationEvent
{
    public override string EventType => "ToolCall";

    public string ToolName { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public Dictionary<string, object?> Parameters { get; set; } = new();
}

/// <summary>
/// Event fired when tool execution starts with actual parameters
/// </summary>
public class ToolExecutionStartEvent : InstrumentationEvent
{
    public override string EventType => "ToolExecutionStart";

    public string ToolName { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public Dictionary<string, object?> Parameters { get; set; } = new();
}

/// <summary>
/// Event fired when a tool execution completes
/// </summary>
public class ToolCompleteEvent : InstrumentationEvent
{
    public override string EventType => "ToolComplete";

    public Guid CallEventId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Result { get; set; }
    public object? ResultData { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Event fired when tool results are sent back to the LLM
/// </summary>
public class ToolResultToLlmEvent : InstrumentationEvent
{
    public override string EventType => "ToolResultToLlm";

    public string ToolName { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Result { get; set; }
    public int ResultLength { get; set; }
    public bool HasStructuredData { get; set; }
    public string? DataType { get; set; }
    public object? StructuredData { get; set; }
}

/// <summary>
/// Event fired when agent state changes
/// </summary>
public class StateChangeEvent : InstrumentationEvent
{
    public override string EventType => "StateChange";

    public string ChangeType { get; set; } = string.Empty;
    public int TurnIndex { get; set; }
    public Dictionary<string, string> WorkingMemory { get; set; } = new();
    public List<string> Subgoals { get; set; } = new();
}

/// <summary>
/// Event fired when a critique is generated
/// </summary>
public class CritiqueEvent : InstrumentationEvent
{
    public override string EventType => "Critique";

    public bool GoalSatisfied { get; set; }
    public string Assessment { get; set; } = string.Empty;
    public List<string> KnownGaps { get; set; } = new();
    public string Recommendation { get; set; } = string.Empty;
}

/// <summary>
/// Summary of a conversation message (to avoid sending full content)
/// </summary>
public class MessageSummary
{
    public string Role { get; set; } = string.Empty;
    public int Length { get; set; }
    public string Preview { get; set; } = string.Empty;
    public bool HasToolCalls { get; set; }
    public int ToolCallCount { get; set; }
}

/// <summary>
/// Event for general diagnostic/debug information
/// </summary>
public class DiagnosticEvent : InstrumentationEvent
{
    public override string EventType => "Diagnostic";

    public string Level { get; set; } = "Info"; // Info, Warning, Error
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object?> Data { get; set; } = new();
}
