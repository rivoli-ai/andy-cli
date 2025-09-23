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

        // Create tool registry adapter
        var toolRegistryAdapter = new ToolRegistryAdapter(toolRegistry, toolExecutor, logger as ILogger<ToolRegistryAdapter>);

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
        _assistant.TurnStarted += (sender, e) =>
        {
            _logger?.LogInformation("Turn {TurnNumber} started for conversation {ConversationId}",
                e.TurnNumber, e.ConversationId);
        };

        _assistant.ToolExecutionStarted += (sender, e) =>
        {
            _feed.AddMarkdownRich($"🔧 Executing tool: **{e.ToolName}**");
        };

        _assistant.ToolExecutionCompleted += (sender, e) =>
        {
            if (e.IsError)
            {
                _feed.AddMarkdownRich($"❌ Tool **{e.ToolCall.Name}** failed: {e.Result.ResultJson}");
            }
            else
            {
                _logger?.LogDebug("Tool {ToolName} completed in {Duration}ms",
                    e.ToolCall.Name, e.Duration.TotalMilliseconds);
            }
        };

        _assistant.ErrorOccurred += (sender, e) =>
        {
            _logger?.LogError(e.Exception, "Error in conversation {ConversationId}: {Context}",
                e.ConversationId, e.Context);
            _feed.AddMarkdownRich($"❌ Error: {e.Exception.Message}");
        };

        _assistant.ToolNotFound += (sender, e) =>
        {
            _logger?.LogWarning("Tool not found: {ToolName} for call {CallId}", e.ToolName, e.CallId);
            _feed.AddMarkdownRich($"⚠️ Tool not found: **{e.ToolName}**");
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
            _feed.AddMarkdownRich($"❌ Error: {ex.Message}");
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