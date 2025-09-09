using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Andy.Llm;
using Andy.Llm.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// Enhanced context manager with proper tool call tracking and orphan cleanup
/// </summary>
public class EnhancedContextManager
{
    private readonly List<EnhancedContextEntry> _history = new();
    private readonly int _maxTokens;
    private readonly int _compressionThreshold;
    private readonly ILogger<EnhancedContextManager>? _logger;
    private string _systemPrompt;

    public EnhancedContextManager(
        string systemPrompt,
        int maxTokens = 8000,
        int compressionThreshold = 6000,
        ILogger<EnhancedContextManager>? logger = null)
    {
        _systemPrompt = systemPrompt;
        _maxTokens = maxTokens;
        _compressionThreshold = compressionThreshold;
        _logger = logger;
    }

    /// <summary>
    /// Add a user message to the context
    /// </summary>
    public void AddUserMessage(string message)
    {
        _logger?.LogDebug("Adding user message of length {Length}", message.Length);

        _history.Add(new EnhancedContextEntry
        {
            Role = MessageRole.User,
            Content = message,
            Timestamp = DateTime.UtcNow,
            TokenEstimate = EstimateTokens(message)
        });
    }

    /// <summary>
    /// Add an assistant message with optional tool calls
    /// </summary>
    public void AddAssistantMessage(string message, List<TrackedToolCall>? toolCalls = null)
    {
        _logger?.LogDebug("Adding assistant message with {Count} tool calls", toolCalls?.Count ?? 0);

        _history.Add(new EnhancedContextEntry
        {
            Role = MessageRole.Assistant,
            Content = message,
            Timestamp = DateTime.UtcNow,
            TokenEstimate = EstimateTokens(message),
            ToolCalls = toolCalls
        });
    }

    /// <summary>
    /// Add a tool response to the context
    /// </summary>
    public void AddToolResponse(string toolId, string callId, string result)
    {
        _logger?.LogDebug("Adding tool response for {ToolId} with call ID {CallId}", toolId, callId);

        _history.Add(new EnhancedContextEntry
        {
            Role = MessageRole.Tool,
            Content = result,
            Timestamp = DateTime.UtcNow,
            TokenEstimate = EstimateTokens(result),
            ToolId = toolId,
            ToolCallId = callId,
            ToolResult = result
        });
    }

    /// <summary>
    /// Get the current conversation context for LLM with orphan cleanup
    /// </summary>
    public ConversationContext GetContext()
    {
        var context = new ConversationContext
        {
            SystemInstruction = _systemPrompt
        };

        // Clean up orphaned tool calls before building context
        var cleanedHistory = CleanOrphanedToolCalls(_history);

        // Check if we need compression
        var totalTokens = EstimateTokens(_systemPrompt);
        foreach (var entry in cleanedHistory)
        {
            totalTokens += entry.TokenEstimate;
        }

        if (totalTokens > _compressionThreshold)
        {
            _logger?.LogDebug("Compressing context: {TotalTokens} > {Threshold}", totalTokens, _compressionThreshold);
            context = CompressContext(cleanedHistory);
        }
        else
        {
            // Add all messages
            foreach (var entry in cleanedHistory)
            {
                AddEntryToContext(context, entry);
            }
        }

        return context;
    }

    /// <summary>
    /// Clean up orphaned tool calls to prevent API errors
    /// </summary>
    private List<EnhancedContextEntry> CleanOrphanedToolCalls(List<EnhancedContextEntry> history)
    {
        var cleaned = new List<EnhancedContextEntry>();
        var toolCallIds = new HashSet<string>();
        var toolResponseIds = new HashSet<string>();

        // First pass: collect all tool call IDs and response IDs
        foreach (var entry in history)
        {
            if (entry.Role == MessageRole.Assistant && entry.ToolCalls != null)
            {
                foreach (var toolCall in entry.ToolCalls)
                {
                    toolCallIds.Add(toolCall.CallId);
                }
            }
            else if (entry.Role == MessageRole.Tool && !string.IsNullOrEmpty(entry.ToolCallId))
            {
                toolResponseIds.Add(entry.ToolCallId);
            }
        }

        _logger?.LogDebug("Found {CallCount} tool calls and {ResponseCount} responses",
            toolCallIds.Count, toolResponseIds.Count);

        // Second pass: filter out orphaned entries
        foreach (var entry in history)
        {
            if (entry.Role == MessageRole.Assistant && entry.ToolCalls != null)
            {
                // Filter out tool calls that don't have responses
                var validToolCalls = entry.ToolCalls
                    .Where(tc => toolResponseIds.Contains(tc.CallId))
                    .ToList();

                if (validToolCalls.Any())
                {
                    // Keep the message with only valid tool calls
                    var cleanedEntry = entry.Clone();
                    cleanedEntry.ToolCalls = validToolCalls;
                    cleaned.Add(cleanedEntry);
                    _logger?.LogDebug("Kept assistant message with {Count} valid tool calls", validToolCalls.Count);
                }
                else if (!string.IsNullOrWhiteSpace(entry.Content))
                {
                    // Keep the message without tool calls if it has content
                    var cleanedEntry = entry.Clone();
                    cleanedEntry.ToolCalls = null;
                    cleaned.Add(cleanedEntry);
                    _logger?.LogDebug("Kept assistant message without tool calls (has content)");
                }
                // Skip if no valid tool calls and no content
            }
            else if (entry.Role == MessageRole.Tool && !string.IsNullOrEmpty(entry.ToolCallId))
            {
                // Only keep tool responses that have corresponding calls
                if (toolCallIds.Contains(entry.ToolCallId))
                {
                    cleaned.Add(entry);
                    _logger?.LogDebug("Kept tool response for call ID {CallId}", entry.ToolCallId);
                }
                else
                {
                    _logger?.LogWarning("Dropping orphaned tool response for call ID {CallId}", entry.ToolCallId);
                }
            }
            else
            {
                // Keep all other messages as-is
                cleaned.Add(entry);
            }
        }

        return cleaned;
    }

    /// <summary>
    /// Add a context entry to the conversation context
    /// </summary>
    private void AddEntryToContext(ConversationContext context, EnhancedContextEntry entry)
    {
        if (entry.Role == MessageRole.User)
        {
            context.AddUserMessage(entry.Content);
        }
        else if (entry.Role == MessageRole.Assistant)
        {
            if (entry.ToolCalls != null && entry.ToolCalls.Any())
            {
                // Convert to FunctionCall format
                var functionCalls = entry.ToolCalls.Select(tc => new FunctionCall
                {
                    Id = tc.CallId,
                    Name = tc.ToolId,
                    Arguments = tc.Parameters
                }).ToList();

                context.AddAssistantMessageWithToolCalls(entry.Content, functionCalls);
            }
            else
            {
                context.AddAssistantMessage(entry.Content);
            }
        }
        else if (entry.Role == MessageRole.Tool)
        {
            context.AddToolResponse(
                entry.ToolId ?? "tool",
                entry.ToolCallId ?? "unknown",
                entry.Content);
        }
    }

    /// <summary>
    /// Compress context when approaching token limit
    /// </summary>
    private ConversationContext CompressContext(List<EnhancedContextEntry> history)
    {
        var context = new ConversationContext
        {
            SystemInstruction = _systemPrompt
        };

        // Keep the most recent messages
        var recentMessages = history.TakeLast(10).ToList();
        var olderMessages = history.Take(history.Count - 10).ToList();

        if (olderMessages.Any())
        {
            // Create a summary of older messages
            var summary = SummarizeMessages(olderMessages);
            context.AddAssistantMessage($"[Previous conversation summary: {summary}]");
        }

        // Add recent messages
        foreach (var entry in recentMessages)
        {
            AddEntryToContext(context, entry);
        }

        return context;
    }

    /// <summary>
    /// Summarize a list of messages
    /// </summary>
    private string SummarizeMessages(List<EnhancedContextEntry> messages)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"Discussed {messages.Count} messages:");

        // Group by tool usage
        var toolCalls = messages.Where(m => m.Role == MessageRole.Tool).ToList();
        if (toolCalls.Any())
        {
            var toolGroups = toolCalls.GroupBy(t => t.ToolId);
            summary.AppendLine($"- Executed tools: {string.Join(", ", toolGroups.Select(g => $"{g.Key} ({g.Count()}x)"))}");
        }

        var userMessages = messages.Where(m => m.Role == MessageRole.User).ToList();
        if (userMessages.Any())
        {
            summary.AppendLine($"- User asked about {userMessages.Count} topics");
        }

        return summary.ToString();
    }

    /// <summary>
    /// Estimate token count (rough approximation)
    /// </summary>
    private int EstimateTokens(string text)
    {
        // Rough estimate: 1 token ≈ 4 characters
        return text.Length / 4;
    }

    /// <summary>
    /// Clear the conversation history
    /// </summary>
    public void Clear()
    {
        _logger?.LogDebug("Clearing conversation history");
        _history.Clear();
    }

    /// <summary>
    /// Update the system prompt
    /// </summary>
    public void UpdateSystemPrompt(string prompt)
    {
        _logger?.LogDebug("Updating system prompt");
        _systemPrompt = prompt;
    }

    /// <summary>
    /// Get conversation statistics
    /// </summary>
    public ContextStats GetStats()
    {
        return new ContextStats
        {
            MessageCount = _history.Count + 1, // +1 for system prompt
            EstimatedTokens = _history.Sum(h => h.TokenEstimate) + EstimateTokens(_systemPrompt),
            ToolCallCount = _history.Count(h => h.Role == MessageRole.Tool),
            OldestMessage = _history.FirstOrDefault()?.Timestamp,
            NewestMessage = _history.LastOrDefault()?.Timestamp
        };
    }
}

/// <summary>
/// Enhanced context entry with better tool call tracking
/// </summary>
public class EnhancedContextEntry
{
    public MessageRole Role { get; set; }
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