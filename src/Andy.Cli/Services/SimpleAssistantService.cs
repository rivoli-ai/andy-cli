using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// Assistant service using Andy.Engine.SimpleAgent for reliable, direct LLM function calling.
/// </summary>
public class SimpleAssistantService : IDisposable
{
    private readonly SimpleAgent _agent;
    private readonly FeedView _feed;
    private readonly ILogger<SimpleAssistantService>? _logger;
    private readonly string _modelName;
    private readonly string _providerName;

    public SimpleAssistantService(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        string modelName,
        string providerName,
        ILogger<SimpleAssistantService>? logger = null)
    {
        _feed = feed;
        _logger = logger;
        _modelName = modelName;
        _providerName = providerName;

        // Log initialization details
        _logger?.LogInformation("Initializing SimpleAssistantService with provider: {Provider}, model: {Model}",
            providerName, modelName);

        // Log tool registry information
        var registeredTools = toolRegistry.GetTools();
        var toolNames = registeredTools.Select(t => t.Metadata.Id).ToList();
        _logger?.LogInformation("Tool registry initialized with {ToolCount} tools: {Tools}",
            toolNames.Count, string.Join(", ", toolNames));

        // Build system prompt using the new SystemPrompts helper
        var systemPrompt = SystemPrompts.GetDefaultCliPrompt();

        // Create the SimpleAgent
        _agent = new SimpleAgent(
            llmProvider,
            toolRegistry,
            toolExecutor,
            systemPrompt,
            maxTurns: 10,
            logger: logger as ILogger<SimpleAgent>
        );

        // Subscribe to agent events for UI updates
        _agent.ToolCalled += (sender, e) =>
        {
            _logger?.LogInformation("Tool called: {ToolName}", e.ToolName);
            _feed.AddMarkdownRich($"🔧 Tool: {e.ToolName}");
        };
    }

    /// <summary>
    /// Process a user message
    /// </summary>
    public async Task<string> ProcessMessageAsync(
        string userMessage,
        bool enableStreaming = false, // Ignored for now - streaming not yet implemented
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Create new content pipeline for this request
            var processor = new MarkdownContentProcessor();
            var sanitizer = new TextContentSanitizer();
            var renderer = new FeedContentRenderer(_feed, _logger as ILogger<FeedContentRenderer>);
            var pipeline = new ContentPipeline.ContentPipeline(processor, sanitizer, renderer, _logger as ILogger<ContentPipeline.ContentPipeline>);

            // Local shortcut: answer simple environment questions without LLM
            if (LooksLikeCurrentDirectoryQuery(userMessage))
            {
                var cwd = Directory.GetCurrentDirectory();
                var msg = $"Current directory: {cwd}";
                pipeline.AddRawContent(msg);
                await pipeline.FinalizeAsync();
                pipeline.Dispose();
                return msg;
            }

            // Show loading indicator
            _feed.AddMarkdownRich($"[...] Processing request...");

            // Process message through SimpleAgent
            var result = await _agent.ProcessMessageAsync(userMessage, cancellationToken);

            _logger?.LogInformation("Agent result - Success: {Success}, Response: '{Response}', StopReason: {StopReason}",
                result.Success, result.Response, result.StopReason);

            // Debug: Always show result details
            _feed.AddMarkdownRich($"[DEBUG] Success={result.Success}, Response.Length={result.Response?.Length ?? 0}, StopReason={result.StopReason}");

            // Add response to pipeline
            if (!string.IsNullOrEmpty(result.Response))
            {
                pipeline.AddRawContent(result.Response);
            }
            else if (!result.Success)
            {
                // Error case - show the error
                _logger?.LogWarning("Agent failed. StopReason: {StopReason}", result.StopReason);
                pipeline.AddRawContent($"**Error**: Agent failed with StopReason: {result.StopReason}");
            }
            else
            {
                // No response content - this is unexpected
                _logger?.LogWarning("Agent returned empty response. Success: {Success}, StopReason: {StopReason}",
                    result.Success, result.StopReason);
                pipeline.AddRawContent($"[No response received. StopReason: {result.StopReason}]");
            }

            // Show context stats
            var stats = GetContextStats();
            pipeline.AddSystemMessage("", SystemMessageType.Context, priority: 1999);
            var contextInfo = $"Context: {stats.TurnCount} turns, ~{stats.EstimatedTokens} tokens, Duration: {stats.TotalDuration.TotalSeconds:F1}s";
            pipeline.AddSystemMessage(contextInfo, SystemMessageType.Context, priority: 2000);

            await pipeline.FinalizeAsync();
            pipeline.Dispose();

            return result.Response ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to process message");

            // Log full error details for debugging
            if (ex.Message.Contains("Cerebras") || _providerName.Contains("cerebras", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError("Cerebras provider error - Message: {Message}", ex.Message);
                _logger?.LogError("Cerebras provider error - Inner: {Inner}", ex.InnerException?.Message);
                _logger?.LogError("Cerebras provider error - Stack: {Stack}", ex.StackTrace);
            }

            _feed.AddMarkdownRich($"[ERROR] {ex.Message}");

            // Show more details for provider-specific errors
            if (_providerName.Contains("cerebras", StringComparison.OrdinalIgnoreCase))
            {
                _feed.AddMarkdownRich($"        Provider: {_providerName}");
                _feed.AddMarkdownRich($"        Model: {_modelName}");
                if (ex.InnerException != null)
                {
                    _feed.AddMarkdownRich($"        Details: {ex.InnerException.Message}");
                }
            }

            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Clear the conversation context
    /// </summary>
    public void ClearContext()
    {
        _agent.ClearHistory();
        _logger?.LogInformation("Conversation context cleared");
    }

    public class ContextStats
    {
        public int TurnCount { get; set; }
        public int EstimatedTokens { get; set; }
        public TimeSpan TotalDuration { get; set; }
    }

    /// <summary>
    /// Get context statistics from the agent
    /// </summary>
    public ContextStats GetContextStats()
    {
        var history = _agent.GetHistory();
        var turnCount = history.Count / 2; // Rough estimate: user + assistant per turn

        // Rough token estimation
        var estimatedTokens = history.Sum(m => m.Content.Length / 4); // Simple char-to-token ratio

        return new ContextStats
        {
            TurnCount = turnCount,
            EstimatedTokens = estimatedTokens,
            TotalDuration = TimeSpan.Zero // Duration tracked per-message in SimpleAgent
        };
    }

    private bool LooksLikeCurrentDirectoryQuery(string userMessage)
    {
        var s = userMessage?.ToLowerInvariant().Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(s)) return false;
        // Only treat as local CWD query if it's a short, direct question
        if (s.Length > 80) return false;
        bool mentionsCwd = s.Contains("current directory") || s.Contains("cwd") || s.Contains("pwd");
        if (!mentionsCwd) return false;
        // If also asking about repo/files/contents, defer to tools/LLM
        if (s.Contains("repo") || s.Contains("repository") || s.Contains("files") ||
            s.Contains("file") || s.Contains("contents") || s.Contains("what can you tell"))
            return false;
        return true;
    }

    public void Dispose()
    {
        _agent?.Dispose();
    }
}
