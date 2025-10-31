using Andy.Cli.Widgets;
using Andy.Engine.Interactive;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// User interface implementation that integrates andy-engine's InteractiveAgent
/// with andy-cli's EnhancedFeedView widget
/// </summary>
public class FeedUserInterface : IUserInterface
{
    private readonly EnhancedFeedView _feed;
    private readonly ILogger<FeedUserInterface>? _logger;
    private readonly Dictionary<string, DateTime> _toolStartTimes = new();

    public FeedUserInterface(EnhancedFeedView feed, ILogger<FeedUserInterface>? logger = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _logger = logger;
    }

    public Task ShowAsync(string message, MessageType messageType = MessageType.Information, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Task.CompletedTask;

        var prefix = messageType switch
        {
            MessageType.Error => "[ERROR] ",
            MessageType.Warning => "[WARNING] ",
            MessageType.Success => "[SUCCESS] ",
            MessageType.Information => "",
            _ => ""
        };

        _feed.AddMarkdownRich($"{prefix}{message}");
        _logger?.LogDebug("Showed message: {Type} - {Message}", messageType, message);

        return Task.CompletedTask;
    }

    public Task ShowContentAsync(string content, ContentType contentType = ContentType.Markdown, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Task.CompletedTask;

        // FeedView handles markdown rendering
        _feed.AddMarkdownRich(content);
        _logger?.LogDebug("Showed content: {Type}, length: {Length}", contentType, content.Length);

        return Task.CompletedTask;
    }

    public Task<string> ChooseAsync(string question, IList<string> options, CancellationToken cancellationToken = default)
    {
        // In andy-cli, we don't support agent-initiated choice prompts
        // Return the first option as default if available
        _logger?.LogWarning("ChooseAsync called but andy-cli doesn't support agent-initiated choice prompts");

        var defaultChoice = options.Count > 0 ? options[0] : string.Empty;
        _feed.AddMarkdownRich($"[CHOICE] {question}");
        foreach (var option in options)
        {
            _feed.AddMarkdownRich($"  - {option}");
        }
        _feed.AddMarkdownRich($"*Auto-selected: {defaultChoice}*");

        return Task.FromResult(defaultChoice);
    }

    public Task ShowProgressAsync(string message, bool isComplete = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Task.CompletedTask;

        // Suppress internal agent turn messages - they're confusing for CLI users
        if (message.StartsWith("Turn") && message.Contains("started"))
            return Task.CompletedTask;

        if (message.StartsWith("Turn") && message.Contains("completed"))
            return Task.CompletedTask;

        // Enhance tool execution messages
        if (message.StartsWith("Using tool:"))
        {
            var toolName = message.Replace("Using tool:", "").Trim();

            // Extract tool name and parameters if available
            var toolDisplayName = toolName;
            var toolId = toolName.ToLower().Replace(" ", "_");

            // Store start time for duration calculation
            _toolStartTimes[toolId] = DateTime.UtcNow;

            // Parse parameters from message if available
            Dictionary<string, object?>? parameters = null;
            if (message.Contains("with"))
            {
                var paramStart = message.IndexOf("with") + 4;
                if (paramStart < message.Length)
                {
                    var paramStr = message.Substring(paramStart).Trim();
                    // Try to parse simple key=value pairs
                    parameters = ParseToolParameters(paramStr);
                }
            }

            // Show animated tool start with parameters
            _feed.AddToolExecutionStart(toolId, toolDisplayName, parameters);

            // Log for detailed tracing
            _logger?.LogInformation("[TOOL_START] {ToolName} started execution", toolDisplayName);
            if (parameters != null)
            {
                foreach (var param in parameters.Take(3))
                {
                    _logger?.LogDebug("  Parameter {Key}: {Value}", param.Key, param.Value);
                }
            }

            return Task.CompletedTask;
        }

        // Handle tool completion messages
        if (message.StartsWith("Tool completed:") || message.StartsWith("Tool failed:"))
        {
            var success = message.StartsWith("Tool completed:");
            var toolInfo = message.Replace("Tool completed:", "").Replace("Tool failed:", "").Trim();
            var toolId = toolInfo.ToLower().Replace(" ", "_");

            // Calculate duration if we have a start time
            string duration = "";
            TimeSpan? elapsed = null;
            if (_toolStartTimes.TryGetValue(toolId, out var startTime))
            {
                elapsed = DateTime.UtcNow - startTime;
                duration = FormatDuration(elapsed.Value);
                _toolStartTimes.Remove(toolId);
            }

            // Extract result summary from message if available
            string? resultSummary = null;
            if (message.Contains("Result:"))
            {
                var resultStart = message.IndexOf("Result:") + 7;
                if (resultStart < message.Length)
                {
                    resultSummary = message.Substring(resultStart).Trim();
                }
            }

            _feed.AddToolExecutionComplete(toolId, success, duration, resultSummary);

            // Log completion details
            _logger?.LogInformation("[TOOL_END] {ToolId} {Status} in {Duration}",
                toolId, success ? "completed" : "failed",
                elapsed?.TotalSeconds.ToString("F1") ?? "unknown" + "s");
            if (!string.IsNullOrEmpty(resultSummary))
            {
                _logger?.LogDebug("  Result: {Result}", resultSummary);
            }

            return Task.CompletedTask;
        }

        // Handle tool detail messages (new)
        if (message.StartsWith("Tool detail:"))
        {
            var detail = message.Replace("Tool detail:", "").Trim();
            // Extract tool ID from detail if possible
            var parts = detail.Split(':', 2);
            if (parts.Length == 2)
            {
                var toolId = parts[0].Trim().ToLower().Replace(" ", "_");
                var detailText = parts[1].Trim();
                _feed.AddToolExecutionDetail(toolId, detailText);
                _logger?.LogDebug("[TOOL_DETAIL] {ToolId}: {Detail}", toolId, detailText);
            }
            return Task.CompletedTask;
        }

        var prefix = isComplete ? "[DONE] " : "[...] ";
        _feed.AddMarkdownRich($"{prefix}{message}");

        return Task.CompletedTask;
    }

    public Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
    {
        // In andy-cli, user input comes from the main loop, not from the agent
        // This method shouldn't be called in our use case
        _logger?.LogWarning("AskAsync called but andy-cli handles user input externally");
        throw new NotSupportedException("Interactive prompting is not supported in andy-cli. User input is handled by the main CLI loop.");
    }

    public Task<bool> ConfirmAsync(string message, bool defaultValue = false, CancellationToken cancellationToken = default)
    {
        // In andy-cli, we don't do confirmations within the agent
        // This method shouldn't be called in our use case
        _logger?.LogWarning("ConfirmAsync called but andy-cli doesn't support agent-initiated confirmations");

        // Return default value - the agent should not require user confirmations mid-execution
        return Task.FromResult(defaultValue);
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds < 1000)
            return $"{elapsed.TotalMilliseconds:0}ms";
        else if (elapsed.TotalSeconds < 60)
            return $"{elapsed.TotalSeconds:0.0}s";
        else
            return $"{elapsed.TotalMinutes:0.0}m";
    }

    private static Dictionary<string, object?> ParseToolParameters(string paramStr)
    {
        var parameters = new Dictionary<string, object?>();

        // Simple parsing of key=value pairs
        var pairs = paramStr.Split(',');
        foreach (var pair in pairs)
        {
            var kvp = pair.Split('=', 2);
            if (kvp.Length == 2)
            {
                var key = kvp[0].Trim();
                var value = kvp[1].Trim();

                // Remove quotes if present
                if ((value.StartsWith('"') && value.EndsWith('"')) ||
                    (value.StartsWith('\'') && value.EndsWith('\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                parameters[key] = value;
            }
        }

        return parameters;
    }
}
