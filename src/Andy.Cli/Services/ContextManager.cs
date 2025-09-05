using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Andy.Llm;
using Andy.Llm.Models;

namespace Andy.Cli.Services;

/// <summary>
/// Manages conversation context with compression and history
/// </summary>
public class ContextManager
{
    private readonly List<ContextEntry> _history = new();
    private readonly int _maxTokens;
    private readonly int _compressionThreshold;
    private string _systemPrompt;

    public ContextManager(
        string systemPrompt,
        int maxTokens = 8000,
        int compressionThreshold = 6000)
    {
        _systemPrompt = systemPrompt;
        _maxTokens = maxTokens;
        _compressionThreshold = compressionThreshold;
    }

    /// <summary>
    /// Add a user message to the context
    /// </summary>
    public void AddUserMessage(string message)
    {
        _history.Add(new ContextEntry
        {
            Role = MessageRole.User,
            Content = message,
            Timestamp = DateTime.UtcNow,
            TokenEstimate = EstimateTokens(message)
        });
    }

    /// <summary>
    /// Add an assistant message to the context
    /// </summary>
    public void AddAssistantMessage(string message)
    {
        _history.Add(new ContextEntry
        {
            Role = MessageRole.Assistant,
            Content = message,
            Timestamp = DateTime.UtcNow,
            TokenEstimate = EstimateTokens(message)
        });
    }

    /// <summary>
    /// Add a tool execution to the context
    /// </summary>
    public void AddToolExecution(string toolId, Dictionary<string, object?> parameters, string result)
    {
        var toolMessage = FormatToolExecution(toolId, parameters, result);
        
        _history.Add(new ContextEntry
        {
            Role = MessageRole.Tool,
            Content = toolMessage,
            Timestamp = DateTime.UtcNow,
            TokenEstimate = EstimateTokens(toolMessage),
            ToolId = toolId,
            ToolResult = result
        });
    }

    /// <summary>
    /// Get the current conversation context for LLM
    /// </summary>
    public ConversationContext GetContext()
    {
        var context = new ConversationContext
        {
            SystemInstruction = _systemPrompt
        };

        // Check if we need compression
        var totalTokens = EstimateTokens(_systemPrompt);
        foreach (var entry in _history)
        {
            totalTokens += entry.TokenEstimate;
        }

        if (totalTokens > _compressionThreshold)
        {
            // Compress older messages
            context = CompressContext();
        }
        else
        {
            // Add all messages
            foreach (var entry in _history)
            {
                if (entry.Role == MessageRole.User)
                {
                    context.AddUserMessage(entry.Content);
                }
                else if (entry.Role == MessageRole.Assistant)
                {
                    context.AddAssistantMessage(entry.Content);
                }
                else if (entry.Role == MessageRole.Tool)
                {
                    // Tool messages need to be added as tool responses
                    context.AddToolResponse(entry.ToolId ?? "tool", "call_" + Guid.NewGuid().ToString("N"), entry.Content);
                }
            }
        }

        return context;
    }

    /// <summary>
    /// Compress context when approaching token limit
    /// </summary>
    private ConversationContext CompressContext()
    {
        var context = new ConversationContext
        {
            SystemInstruction = _systemPrompt
        };

        // Keep the most recent messages and summarize older ones
        var recentMessages = _history.TakeLast(10).ToList();
        var olderMessages = _history.Take(_history.Count - 10).ToList();

        if (olderMessages.Any())
        {
            // Create a summary of older messages
            var summary = SummarizeMessages(olderMessages);
            context.AddAssistantMessage($"[Previous conversation summary: {summary}]");
        }

        // Add recent messages
        foreach (var entry in recentMessages)
        {
            if (entry.Role == MessageRole.User)
            {
                context.AddUserMessage(entry.Content);
            }
            else if (entry.Role == MessageRole.Assistant)
            {
                context.AddAssistantMessage(entry.Content);
            }
            else if (entry.Role == MessageRole.Tool)
            {
                context.AddToolResponse(
                    entry.ToolId ?? "tool",
                    "call_" + Guid.NewGuid().ToString("N"),
                    entry.ToolResult ?? entry.Content
                );
            }
        }

        return context;
    }

    /// <summary>
    /// Summarize a list of messages
    /// </summary>
    private string SummarizeMessages(List<ContextEntry> messages)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"Discussed {messages.Count} messages:");
        
        // Group by topic/tool usage
        var toolCalls = messages.Where(m => m.Role == MessageRole.Tool).ToList();
        if (toolCalls.Any())
        {
            var toolGroups = toolCalls.GroupBy(t => t.ToolId);
            summary.AppendLine($"- Executed tools: {string.Join(", ", toolGroups.Select(g => $"{g.Key} ({g.Count()}x)"))}");
        }

        // Extract key topics (simplified - in production use NLP)
        var userMessages = messages.Where(m => m.Role == MessageRole.User).ToList();
        if (userMessages.Any())
        {
            summary.AppendLine($"- User asked about {userMessages.Count} topics");
        }

        return summary.ToString();
    }

    /// <summary>
    /// Format tool execution for context
    /// </summary>
    private string FormatToolExecution(string toolId, Dictionary<string, object?> parameters, string result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Tool Execution: {toolId}]");
        
        if (parameters.Any())
        {
            sb.AppendLine("Parameters:");
            foreach (var param in parameters)
            {
                sb.AppendLine($"  {param.Key}: {param.Value}");
            }
        }
        
        sb.AppendLine("Result:");
        
        // Limit result size for context
        if (result.Length > 500)
        {
            sb.AppendLine(result.Substring(0, 497) + "...");
            sb.AppendLine($"[Truncated - full result was {result.Length} characters]");
        }
        else
        {
            sb.AppendLine(result);
        }
        
        return sb.ToString();
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
        _history.Clear();
    }

    /// <summary>
    /// Update the system prompt
    /// </summary>
    public void UpdateSystemPrompt(string prompt)
    {
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
/// Represents a single entry in the context history
/// </summary>
public class ContextEntry
{
    public MessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public int TokenEstimate { get; set; }
    public string? ToolId { get; set; }
    public string? ToolResult { get; set; }
}

/// <summary>
/// Message role in conversation
/// </summary>
public enum MessageRole
{
    User,
    Assistant,
    Tool,
    System
}

/// <summary>
/// Context statistics
/// </summary>
public class ContextStats
{
    public int MessageCount { get; set; }
    public int EstimatedTokens { get; set; }
    public int ToolCallCount { get; set; }
    public DateTime? OldestMessage { get; set; }
    public DateTime? NewestMessage { get; set; }
}