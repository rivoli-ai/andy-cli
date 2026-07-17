namespace Andy.Cli.Instrumentation;

/// <summary>
/// Produces redacted copies of instrumentation events so that sensitive content
/// (user messages, model responses, tool parameters and results) is not exposed
/// unless the operator has explicitly opted in via
/// <see cref="InstrumentationOptions.IncludeSensitive"/>.
///
/// Non-sensitive metadata (provider, model, tool names, token counts, durations,
/// lengths) is preserved so the dashboard remains useful for diagnostics even when
/// the payloads themselves are hidden.
/// </summary>
public static class InstrumentationRedactor
{
    /// <summary>Placeholder substituted for redacted string content.</summary>
    public const string Placeholder = "[redacted]";

    /// <summary>
    /// Return an event suitable for streaming given the sensitivity setting.
    /// When <paramref name="includeSensitive"/> is true the original event is
    /// returned unchanged; otherwise a redacted copy is produced.
    /// </summary>
    public static InstrumentationEvent ForOutput(InstrumentationEvent evt, bool includeSensitive)
    {
        if (includeSensitive)
        {
            return evt;
        }

        return Redact(evt);
    }

    /// <summary>
    /// Create a redacted copy of the event. The returned instance carries the same
    /// non-sensitive metadata but with sensitive text replaced by a placeholder.
    /// </summary>
    public static InstrumentationEvent Redact(InstrumentationEvent evt)
    {
        switch (evt)
        {
            case LlmRequestEvent e:
                return new LlmRequestEvent
                {
                    Provider = e.Provider,
                    Model = e.Model,
                    UserMessage = string.IsNullOrEmpty(e.UserMessage) ? e.UserMessage : Placeholder,
                    ConversationTurns = e.ConversationTurns,
                    EstimatedInputTokens = e.EstimatedInputTokens,
                    ConversationHistory = e.ConversationHistory
                        .Select(m => new MessageSummary
                        {
                            Role = m.Role,
                            Length = m.Length,
                            Preview = string.IsNullOrEmpty(m.Preview) ? m.Preview : Placeholder,
                            HasToolCalls = m.HasToolCalls,
                            ToolCallCount = m.ToolCallCount
                        })
                        .ToList()
                };

            case LlmResponseEvent e:
                return new LlmResponseEvent
                {
                    RequestId = e.RequestId,
                    Success = e.Success,
                    StopReason = e.StopReason,
                    Response = string.IsNullOrEmpty(e.Response) ? e.Response : Placeholder,
                    ResponseLength = e.ResponseLength,
                    EstimatedOutputTokens = e.EstimatedOutputTokens,
                    Duration = e.Duration,
                    ActualInputTokens = e.ActualInputTokens,
                    ActualOutputTokens = e.ActualOutputTokens
                };

            case ToolCallEvent e:
                return new ToolCallEvent
                {
                    ToolName = e.ToolName,
                    ToolId = e.ToolId,
                    Parameters = RedactParameters(e.Parameters)
                };

            case ToolExecutionStartEvent e:
                return new ToolExecutionStartEvent
                {
                    ToolName = e.ToolName,
                    ToolId = e.ToolId,
                    Parameters = RedactParameters(e.Parameters)
                };

            case ToolCompleteEvent e:
                return new ToolCompleteEvent
                {
                    CallEventId = e.CallEventId,
                    ToolName = e.ToolName,
                    ToolId = e.ToolId,
                    Success = e.Success,
                    Result = string.IsNullOrEmpty(e.Result) ? e.Result : Placeholder,
                    ResultData = null,
                    Duration = e.Duration
                };

            case ToolResultToLlmEvent e:
                return new ToolResultToLlmEvent
                {
                    ToolName = e.ToolName,
                    ToolId = e.ToolId,
                    Success = e.Success,
                    Result = string.IsNullOrEmpty(e.Result) ? e.Result : Placeholder,
                    ResultLength = e.ResultLength,
                    HasStructuredData = e.HasStructuredData,
                    DataType = e.DataType,
                    StructuredData = null
                };

            case StateChangeEvent e:
                return new StateChangeEvent
                {
                    // ChangeType and TurnIndex are structural metadata; WorkingMemory
                    // values and Subgoals are model/user-derived free text and must be
                    // masked. Keys and counts are preserved for diagnostic shape.
                    ChangeType = e.ChangeType,
                    TurnIndex = e.TurnIndex,
                    WorkingMemory = e.WorkingMemory.ToDictionary(kv => kv.Key, _ => Placeholder),
                    Subgoals = e.Subgoals.Select(_ => Placeholder).ToList()
                };

            case CritiqueEvent e:
                return new CritiqueEvent
                {
                    // GoalSatisfied is a boolean signal; Assessment, KnownGaps and
                    // Recommendation are model-authored free text and must be masked.
                    GoalSatisfied = e.GoalSatisfied,
                    Assessment = string.IsNullOrEmpty(e.Assessment) ? e.Assessment : Placeholder,
                    KnownGaps = e.KnownGaps.Select(_ => Placeholder).ToList(),
                    Recommendation = string.IsNullOrEmpty(e.Recommendation) ? e.Recommendation : Placeholder
                };

            case DiagnosticEvent e:
                return new DiagnosticEvent
                {
                    // Level and Source identify the emitting component; Message and Data
                    // may carry model/user-derived text and must be masked.
                    Level = e.Level,
                    Source = e.Source,
                    Message = string.IsNullOrEmpty(e.Message) ? e.Message : Placeholder,
                    Data = e.Data.ToDictionary(kv => kv.Key, _ => (object?)Placeholder)
                };

            // Fail-safe default: any event type not explicitly handled above is masked
            // down to a marker rather than passed through verbatim, so newly added
            // event types cannot leak sensitive payloads before redaction is taught
            // about them.
            default:
                return new RedactedEvent(evt.EventType);
        }
    }

    private static Dictionary<string, object?> RedactParameters(Dictionary<string, object?> parameters)
    {
        // Preserve the parameter names (useful for diagnostics) but hide the values.
        var redacted = new Dictionary<string, object?>();
        foreach (var key in parameters.Keys)
        {
            redacted[key] = Placeholder;
        }
        return redacted;
    }
}
