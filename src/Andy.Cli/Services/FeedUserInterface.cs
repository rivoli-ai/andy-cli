using Andy.Cli.Widgets;
using Andy.Engine.Interactive;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// User interface implementation that integrates andy-engine's InteractiveAgent
/// with andy-cli's FeedView widget
/// </summary>
public class FeedUserInterface : IUserInterface
{
    private readonly FeedView _feed;
    private readonly ILogger<FeedUserInterface>? _logger;

    public FeedUserInterface(FeedView feed, ILogger<FeedUserInterface>? logger = null)
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
            _feed.AddMarkdownRich($"🔧 **Tool**: {toolName}");
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
}
