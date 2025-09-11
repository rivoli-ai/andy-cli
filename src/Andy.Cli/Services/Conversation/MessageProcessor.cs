using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.Conversation;

/// <summary>
/// Handles message processing and context management for AI conversations
/// </summary>
public class MessageProcessor
{
    private readonly ConversationContext _context;
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger? _logger;
    private string _modelName;
    private string _providerName;

    public MessageProcessor(
        ConversationContext context,
        IToolRegistry toolRegistry,
        string modelName,
        string providerName,
        ILogger? logger = null)
    {
        _context = context;
        _toolRegistry = toolRegistry;
        _modelName = modelName;
        _providerName = providerName;
        _logger = logger;
    }

    public void UpdateModelInfo(string modelName, string providerName)
    {
        _modelName = modelName;
        _providerName = NormalizeProviderName(providerName, modelName);
    }

    public bool IsSimpleGreeting(string userMessage)
    {
        var greetings = new[] { "hello", "hi", "hey", "greetings", "good morning", 
                               "good afternoon", "good evening", "howdy" };
        var normalized = userMessage.Trim().ToLower();
        return greetings.Any(g => normalized == g || normalized.StartsWith(g + " ") || 
                                 normalized.StartsWith(g + ",") || normalized.StartsWith(g + "!"));
    }

    public bool LooksLikeRepoExploration(string userMessage)
    {
        var keywords = new[] { "repo", "code", "files", "structure", "project", 
                              "implementation", "class", "function", "method" };
        var normalized = userMessage.ToLower();
        return keywords.Any(k => normalized.Contains(k));
    }

    public LlmRequest CreateRequest(string userMessage, bool includeTools = true)
    {
        // Add user message to context
        _context.AddUserMessage(userMessage);

        // Get tools if needed
        List<ToolDeclaration>? tools = null;
        if (includeTools && !IsSimpleGreeting(userMessage))
        {
            var registeredTools = _toolRegistry.GetTools(enabledOnly: true);
            
            // Limit tools for Cerebras provider
            if (_providerName.Contains("cerebras"))
            {
                var essentialToolIds = new[] { "list_directory", "read_file", "bash_command", "search_files" };
                registeredTools = registeredTools
                    .Where(t => essentialToolIds.Contains(t.Metadata.Id))
                    .Take(4)
                    .ToList();
            }

            if (registeredTools.Any())
            {
                var toolHandler = new ToolHandler(_toolRegistry, null!, _context, null!, _logger);
                tools = toolHandler.GetToolDeclarations(registeredTools.ToList());
            }
        }

        // Create request
        var request = _context.CreateRequest(_modelName);
        if (tools != null && tools.Any())
        {
            request.Tools = tools;
        }

        // Fix any empty message parts
        return FixEmptyMessageParts(request);
    }

    private LlmRequest FixEmptyMessageParts(LlmRequest request)
    {
        if (request.Messages == null) return request;

        foreach (var message in request.Messages)
        {
            if (message.Parts == null || !message.Parts.Any())
            {
                _logger?.LogWarning("Found message with null or empty Parts, adding placeholder");
                message.Parts = new List<MessagePart>
                {
                    new TextPart { Text = " " }
                };
            }
            else
            {
                // Check for empty TextParts
                foreach (var part in message.Parts.OfType<TextPart>())
                {
                    if (string.IsNullOrEmpty(part.Text))
                    {
                        _logger?.LogWarning("Found TextPart with empty Text, adding placeholder");
                        part.Text = " ";
                    }
                }
            }
        }

        return request;
    }

    public void AddAssistantMessage(string content, List<FunctionCall>? functionCalls = null)
    {
        if (functionCalls != null && functionCalls.Any())
        {
            _context.AddAssistantMessageWithToolCalls(content, functionCalls);
        }
        else
        {
            _context.AddAssistantMessage(content);
        }
    }

    public void AddToolResults(List<Message> toolResults)
    {
        foreach (var result in toolResults)
        {
            if (result.Parts != null)
            {
                foreach (var part in result.Parts.OfType<ToolResponsePart>())
                {
                    _context.AddToolResponse(
                        part.ToolName ?? "unknown",
                        part.CallId ?? "",
                        part.Response ?? ""
                    );
                }
            }
        }
    }

    public void ClearContext()
    {
        _context.Clear();
    }

    public ContextStats GetContextStats()
    {
        var totalMessages = _context.Messages.Count;
        var userMessages = _context.Messages.Count(m => m.Role == Andy.Llm.Models.MessageRole.User);
        var assistantMessages = _context.Messages.Count(m => m.Role == Andy.Llm.Models.MessageRole.Assistant);
        var toolMessages = _context.Messages.Count(m => m.Role == Andy.Llm.Models.MessageRole.Tool);

        // Estimate tokens (rough approximation)
        var estimatedTokens = 0;
        foreach (var message in _context.Messages)
        {
            if (message.Parts != null)
            {
                foreach (var part in message.Parts)
                {
                    if (part is Andy.Llm.Models.TextPart textPart)
                    {
                        estimatedTokens += EstimateTokens(textPart.Text ?? "");
                    }
                    else if (part is Andy.Llm.Models.ToolResponsePart toolPart)
                    {
                        estimatedTokens += EstimateTokens(toolPart.Response?.ToString() ?? "");
                    }
                }
            }
        }

        return new ContextStats
        {
            TotalMessages = totalMessages,
            UserMessages = userMessages,
            AssistantMessages = assistantMessages,
            ToolMessages = toolMessages,
            EstimatedTokens = estimatedTokens
        };
    }

    private int EstimateTokens(string text)
    {
        // Rough estimation: 1 token per 4 characters
        return text.Length / 4;
    }

    private string NormalizeProviderName(string providerName, string modelName)
    {
        // Convert provider name to consistent format
        providerName = providerName.ToLower();
        
        // Handle model-specific provider detection
        if (modelName.Contains("gpt", StringComparison.OrdinalIgnoreCase))
        {
            return "openai";
        }
        if (modelName.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "anthropic";
        }
        if (modelName.Contains("llama", StringComparison.OrdinalIgnoreCase) && providerName.Contains("cerebras"))
        {
            return "cerebras";
        }
        if (modelName.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return "qwen";
        }
        
        return providerName;
    }
}

public class ContextStats
{
    public int TotalMessages { get; set; }
    public int UserMessages { get; set; }
    public int AssistantMessages { get; set; }
    public int ToolMessages { get; set; }
    public int EstimatedTokens { get; set; }
}