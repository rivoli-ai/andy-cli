using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Adapters;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Model.Orchestration;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// New conversation service using Andy.Model.Assistant
/// Replaces the old AiConversationService
/// </summary>
public class AssistantService : IDisposable
{
    private readonly Assistant _assistant;
    private readonly Conversation _conversation;
    private readonly FeedView _feed;
    private readonly ILogger<AssistantService>? _logger;
    private readonly string _modelName;
    private readonly string _providerName;
    private readonly CumulativeOutputTracker _outputTracker = new();

    public AssistantService(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        string systemPrompt,
        ILogger<AssistantService>? logger = null,
        string modelName = "",
        string providerName = "")
    {
        _feed = feed;
        _logger = logger;
        _modelName = modelName;
        _providerName = providerName;

        // Log initialization details
        _logger?.LogInformation("Initializing AssistantService with provider: {Provider}, model: {Model}",
            providerName, modelName);

        // Create tool registry adapter with provider name for provider-specific tool filtering
        var toolRegistryAdapter = new ToolRegistryAdapter(toolRegistry, toolExecutor, logger as ILogger<ToolRegistryAdapter>, providerName);

        // Log tool registry information
        var registeredToolNames = toolRegistryAdapter.GetToolRegistry().GetRegisteredToolNames();
        _logger?.LogInformation("Tool registry initialized with {ToolCount} tools: {Tools}",
            registeredToolNames.Count, string.Join(", ", registeredToolNames));

        // Initialize conversation with system prompt
        _conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow
        };

        // Create assistant
        _assistant = new Assistant(_conversation, toolRegistryAdapter.GetToolRegistry(), llmProvider);

        // Subscribe to assistant events for UI updates
        SubscribeToEvents();

        // Content pipeline will be created per message in ProcessMessageAsync
    }

    private void SubscribeToEvents()
    {
        // Turn lifecycle events
        _assistant.TurnStarted += (sender, e) =>
        {
            _logger?.LogInformation("Turn {TurnNumber} started for conversation {ConversationId}",
                e.TurnNumber, e.ConversationId);
            _feed.AddMarkdownRich($"[TURN {e.TurnNumber}] Started");
        };

        _assistant.TurnCompleted += (sender, e) =>
        {
            _logger?.LogInformation("Turn completed for conversation {ConversationId} with {ToolCallCount} tool calls",
                e.ConversationId, e.ToolCallsExecuted);
            var toolInfo = e.ToolCallsExecuted > 0 ? $" | {e.ToolCallsExecuted} tools executed" : "";
            _feed.AddMarkdownRich($"[TURN] Completed in {e.Duration.TotalMilliseconds:N0}ms{toolInfo}");
        };

        // LLM request/response events
        _assistant.LlmRequestStarted += (sender, e) =>
        {
            _logger?.LogInformation("LLM request started: {MessageCount} messages, {ToolCount} tools available",
                e.MessageCount, e.ToolCount);
            var retryInfo = e.IsRetryAfterTools ? " | retry after tools" : "";
            _feed.AddMarkdownRich($"[LLM] Sending: {e.MessageCount} messages, {e.ToolCount} tools available{retryInfo}");
        };

        _assistant.LlmResponseReceived += (sender, e) =>
        {
            var usage = e.Usage;
            var tokenInfo = usage != null ? $"{usage.TotalTokens} tokens" : "tokens unknown";
            var toolInfo = e.HasToolCalls ? " | contains tool calls" : "";
            _logger?.LogInformation("LLM response received: {TokenInfo}, HasToolCalls: {HasToolCalls}",
                tokenInfo, e.HasToolCalls);
            _feed.AddMarkdownRich($"[LLM] Response: {tokenInfo}{toolInfo}");
        };

        // Streaming events
        _assistant.StreamingTokenReceived += (sender, e) =>
        {
            _logger?.LogDebug("Streaming delta received, complete: {IsComplete}", e.IsComplete);
            // Don't display each token to avoid flooding the UI
        };

        // Tool execution events
        _assistant.ToolExecutionStarted += (sender, e) =>
        {
            _logger?.LogInformation("Tool execution started: {ToolName} (ID: {CallId})",
                e.ToolName, e.ToolCall.Id);
            _feed.AddMarkdownRich($"[TOOL] Executing: {e.ToolName}");

            // Display tool arguments if available and not too large
            if (!string.IsNullOrWhiteSpace(e.ToolCall.ArgumentsJson) && e.ToolCall.ArgumentsJson.Length < 200)
            {
                try
                {
                    var args = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(e.ToolCall.ArgumentsJson);
                    // Show inline for simple arguments
                    if (e.ToolCall.ArgumentsJson.Length < 100)
                    {
                        _feed.AddMarkdownRich($"       Args: {e.ToolCall.ArgumentsJson}");
                    }
                    else
                    {
                        var prettyArgs = System.Text.Json.JsonSerializer.Serialize(args, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        _feed.AddMarkdownRich($"       Args:\n```json\n{prettyArgs}\n```");
                    }
                }
                catch
                {
                    _feed.AddMarkdownRich($"       Args: {e.ToolCall.ArgumentsJson}");
                }
            }
        };

        _assistant.ToolExecutionCompleted += (sender, e) =>
        {
            if (e.IsError)
            {
                _logger?.LogError("Tool {ToolName} failed: {Error}",
                    e.ToolCall.Name, e.Result.ResultJson);
                _feed.AddMarkdownRich($"[TOOL] FAILED: {e.ToolCall.Name}");
                _feed.AddMarkdownRich($"       Error: {e.Result.ResultJson}");
            }
            else
            {
                _logger?.LogInformation("Tool {ToolName} completed in {Duration}ms",
                    e.ToolCall.Name, e.Duration.TotalMilliseconds);
                _feed.AddMarkdownRich($"[TOOL] Complete: {e.ToolCall.Name} ({e.Duration.TotalMilliseconds:N0}ms)");

                // Display compact tool result if available
                if (!string.IsNullOrWhiteSpace(e.Result.ResultJson) && e.Result.ResultJson.Length < 200)
                {
                    try
                    {
                        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(e.Result.ResultJson);
                        if (result.ValueKind == System.Text.Json.JsonValueKind.Object && result.TryGetProperty("message", out var msg))
                        {
                            _feed.AddMarkdownRich($"       Result: {msg.GetString()}");
                        }
                        else if (e.Result.ResultJson.Length < 100)
                        {
                            _feed.AddMarkdownRich($"       Result: {e.Result.ResultJson}");
                        }
                    }
                    catch
                    {
                        // Don't show result if parsing fails
                    }
                }
            }
        };

        // Tool validation events
        _assistant.ToolValidationFailed += (sender, e) =>
        {
            var errors = string.Join(", ", e.ValidationErrors);
            _logger?.LogWarning("Tool validation failed: {ToolName} - {ValidationErrors}",
                e.ToolCall.Name, errors);
            _feed.AddMarkdownRich($"[TOOL] Validation failed: {e.ToolCall.Name}");
            _feed.AddMarkdownRich($"       Errors: {errors}");
        };

        // Error events
        _assistant.ErrorOccurred += (sender, e) =>
        {
            _logger?.LogError(e.Exception, "Error in conversation {ConversationId}: {Context}",
                e.ConversationId, e.Context);
            _feed.AddMarkdownRich($"[ERROR] {e.Context}: {e.Exception.Message}");

            // Show more details for debugging
            if (e.Exception.InnerException != null)
            {
                _feed.AddMarkdownRich($"        Inner: {e.Exception.InnerException.Message}");
            }
        };

        _assistant.ToolNotFound += (sender, e) =>
        {
            _logger?.LogWarning("Tool not found: {ToolName} for call {CallId}", e.ToolName, e.CallId);
            _feed.AddMarkdownRich($"[WARNING] Tool not found: {e.ToolName}");
        };
    }

    /// <summary>
    /// Process a user message with tool support
    /// </summary>
    public async Task<string> ProcessMessageAsync(
        string userMessage,
        bool enableStreaming = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Reset output tracker for new message
            _outputTracker.Reset();

            // Create new content pipeline for this request (don't reuse _contentPipeline)
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

            Message assistantResponse;

            if (enableStreaming)
            {
                // Use streaming for real-time responses
                var contentBuilder = new StringBuilder();
                Message? lastMessage = null;

                await foreach (var message in _assistant.RunTurnStreamAsync(userMessage, cancellationToken))
                {
                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        contentBuilder.Append(message.Content);
                        // For streaming, we could update the UI incrementally here
                        // but for now we'll just accumulate the content
                    }
                    lastMessage = message;
                }

                // Use the accumulated content from contentBuilder, not the lastMessage
                assistantResponse = new Message { Role = Role.Assistant, Content = contentBuilder.ToString() };
            }
            else
            {
                // Use non-streaming for complete responses
                assistantResponse = await _assistant.RunTurnAsync(userMessage, cancellationToken);
            }

            // Display the assistant's response
            if (!string.IsNullOrWhiteSpace(assistantResponse.Content))
            {
                pipeline.AddRawContent(assistantResponse.Content);
            }

            // Show context stats
            var stats = GetContextStats();
            var contextInfo = Commands.ConsoleColors.Dim($"Context: {stats.MessageCount} messages, ~{stats.EstimatedTokens} tokens, {stats.ToolCallCount} tool calls");
            pipeline.AddSystemMessage(contextInfo, SystemMessageType.Context, priority: 2000);

            await pipeline.FinalizeAsync();
            pipeline.Dispose();

            return assistantResponse.Content ?? string.Empty;
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
    /// Update the current model and provider information
    /// </summary>
    public void UpdateModelInfo(string modelName, string providerName)
    {
        // This is now handled in the adapter, but we keep the method for compatibility
        _logger?.LogInformation("Model info updated: {Model} / {Provider}", modelName, providerName);
    }

    /// <summary>
    /// Clear the conversation context
    /// </summary>
    public void ClearContext()
    {
        _outputTracker.Reset();
    }

    public class ContextStats
    {
        public int MessageCount { get; set; }
        public int EstimatedTokens { get; set; }
        public int ToolCallCount { get; set; }
    }

    /// <summary>
    /// Get context statistics
    /// </summary>
    public ContextStats GetContextStats()
    {
        var messageCount = 0;
        var toolCallCount = 0;

        foreach (var turn in _conversation.Turns)
        {
            if (turn.UserOrSystemMessage != null) messageCount++;
            if (turn.AssistantMessage != null)
            {
                messageCount++;
                toolCallCount += turn.AssistantMessage.ToolCalls?.Count ?? 0;
            }
            messageCount += turn.ToolMessages.Count;
        }

        // Rough token estimation
        var estimatedTokens = messageCount * 50; // Simple estimation, could be improved

        return new ContextStats
        {
            MessageCount = messageCount,
            EstimatedTokens = estimatedTokens,
            ToolCallCount = toolCallCount
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
        // Nothing to dispose currently
        // Content pipelines are created and disposed per message
    }
}