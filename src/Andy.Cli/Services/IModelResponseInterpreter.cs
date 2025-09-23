namespace Andy.Cli.Services;

/// <summary>
/// Interprets LLM responses based on the model being used
/// </summary>
public interface IModelResponseInterpreter
{
    /// <summary>
    /// Extract tool calls from the model's response
    /// </summary>
    List<ModelToolCall> ExtractToolCalls(string response, string modelName, string provider);

    /// <summary>
    /// Format tool results for sending back to the model
    /// </summary>
    string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results, string modelName, string provider);

    /// <summary>
    /// Check if the response contains fake tool results
    /// </summary>
    bool ContainsFakeToolResults(string response, string modelName);

    /// <summary>
    /// Clean response text for display
    /// </summary>
    string CleanResponseForDisplay(string response, string modelName);
}