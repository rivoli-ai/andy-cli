using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Andy.Tools.Execution;

namespace Andy.Cli.Services;

/// <summary>
/// Represents a chunk of conversation response during streaming
/// </summary>
public class ConversationChunk
{
    public string? TextContent { get; set; }
    public ModelToolCall? ToolCall { get; set; }
    public ToolExecutionResult? ToolResult { get; set; }
    public ChunkType Type { get; set; }
    public bool IsComplete { get; set; }
}

public enum ChunkType
{
    Text,
    ToolCall,
    ToolResult,
    Error,
    Metadata
}

/// <summary>
/// Enhanced conversation service with Qwen-specific handling and streaming support
/// Based on qwen-code's conversation patterns
/// </summary>
public class QwenConversationService
{
    private readonly LlmClient _llmClient;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly IQwenResponseParser _parser;
    private readonly IToolCallValidator _validator;
    private readonly StreamingToolCallAccumulator _accumulator;
    private readonly IJsonRepairService _jsonRepair;
    private readonly ToolExecutionService _toolExecutionService;
    private readonly ContextManager _contextManager;
    private readonly FeedView _feed;
    private readonly ILogger<QwenConversationService>? _logger;
    
    private string _currentModel = "";
    private string _currentProvider = "";
    private readonly int _maxIterations = 5;
    private readonly TimeSpan _streamTimeout = TimeSpan.FromMinutes(2);

    public QwenConversationService(
        LlmClient llmClient,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        IQwenResponseParser parser,
        IToolCallValidator validator,
        StreamingToolCallAccumulator accumulator,
        IJsonRepairService jsonRepair,
        FeedView feed,
        string systemPrompt,
        string modelName = "",
        string providerName = "",
        ILogger<QwenConversationService>? logger = null)
    {
        _llmClient = llmClient;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _parser = parser;
        _validator = validator;
        _accumulator = accumulator;
        _jsonRepair = jsonRepair;
        _feed = feed;
        _logger = logger;
        _contextManager = new ContextManager(systemPrompt);
        _toolExecutionService = new ToolExecutionService(toolRegistry, toolExecutor, feed);
        _currentModel = modelName;
        _currentProvider = providerName;
    }

    /// <summary>
    /// Update the current model and provider information
    /// </summary>
    public void UpdateModelInfo(string modelName, string providerName)
    {
        _currentModel = modelName;
        _currentProvider = providerName;
        _logger?.LogDebug("Updated model info: {Model} from {Provider}", modelName, providerName);
    }

    /// <summary>
    /// Process a message with streaming support and tool execution
    /// </summary>
    public async IAsyncEnumerable<ConversationChunk> ProcessStreamingAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Add user message to context
        _contextManager.AddUserMessage(userMessage);
        
        // Track iterations to prevent infinite loops
        var iteration = 0;
        var continueProcessing = true;
        
        while (continueProcessing && iteration < _maxIterations)
        {
            iteration++;
            _logger?.LogDebug("Processing iteration {Iteration} of {Max}", iteration, _maxIterations);
            
            // Clear accumulator for new response
            _accumulator.Clear();
            
            // Create request with available tools
            var availableTools = _toolRegistry.GetTools(enabledOnly: true).ToList();
            var request = CreateRequestWithTools(availableTools);
            
            // Stream response from LLM
            var streamingResponse = StreamLlmResponseAsync(request, cancellationToken);
            var parsedResponse = await _parser.ParseStreamingAsync(streamingResponse, cancellationToken);
            
            // Yield text content if any
            if (!string.IsNullOrWhiteSpace(parsedResponse.TextContent))
            {
                yield return new ConversationChunk
                {
                    TextContent = parsedResponse.TextContent,
                    Type = ChunkType.Text,
                    IsComplete = !parsedResponse.HasToolCalls
                };
                
                _contextManager.AddAssistantMessage(parsedResponse.TextContent);
            }
            
            // Process tool calls if any
            if (parsedResponse.HasToolCalls)
            {
                _logger?.LogDebug("Processing {Count} tool calls", parsedResponse.ToolCalls.Count);
                
                var toolResults = new List<string>();
                
                foreach (var toolCall in parsedResponse.ToolCalls)
                {
                    // Validate and repair tool call
                    var toolMetadata = _toolRegistry.GetTool(toolCall.ToolId)?.Metadata;
                    if (toolMetadata == null)
                    {
                        _logger?.LogWarning("Tool '{ToolId}' not found in registry", toolCall.ToolId);
                        
                        yield return new ConversationChunk
                        {
                            TextContent = $"Error: Tool '{toolCall.ToolId}' not found",
                            Type = ChunkType.Error,
                            IsComplete = false
                        };
                        
                        toolResults.Add($"Error: Tool '{toolCall.ToolId}' not found");
                        continue;
                    }
                    
                    var validation = _validator.Validate(toolCall, toolMetadata);
                    var callToExecute = validation.IsValid ? toolCall : validation.RepairedCall ?? toolCall;
                    
                    // Log validation warnings
                    foreach (var warning in validation.Warnings)
                    {
                        _logger?.LogWarning("Validation warning for {Tool}: {Message}", 
                            toolCall.ToolId, warning.Message);
                    }
                    
                    // Yield tool call chunk
                    yield return new ConversationChunk
                    {
                        ToolCall = callToExecute,
                        Type = ChunkType.ToolCall,
                        IsComplete = false
                    };
                    
                    // Execute tool
                    var result = await _toolExecutionService.ExecuteToolAsync(
                        callToExecute.ToolId,
                        callToExecute.Parameters,
                        cancellationToken);
                    
                    // Yield tool result chunk
                    yield return new ConversationChunk
                    {
                        ToolResult = result,
                        Type = ChunkType.ToolResult,
                        IsComplete = false
                    };
                    
                    // Collect result for context
                    toolResults.Add(result.IsSuccessful 
                        ? result.FullOutput ?? "No output"
                        : $"Error: {result.ErrorMessage ?? "Unknown error"}");
                }
                
                // Add tool results to context
                if (toolResults.Any())
                {
                    foreach (var i in Enumerable.Range(0, Math.Min(parsedResponse.ToolCalls.Count, toolResults.Count)))
                    {
                        _contextManager.AddToolExecution(
                            parsedResponse.ToolCalls[i].ToolId,
                            parsedResponse.ToolCalls[i].Parameters,
                            toolResults[i]);
                    }
                }
            }
            else
            {
                // No tool calls, we're done
                continueProcessing = false;
            }
            
            // Check for errors that should stop processing
            if (parsedResponse.HasErrors)
            {
                foreach (var error in parsedResponse.Errors)
                {
                    _logger?.LogError("Parse error: {Type} - {Message}", error.Type, error.Message);
                    
                    if (error.Type == ParseErrorType.IncompleteResponse)
                    {
                        // Try to continue with partial response
                        _logger?.LogWarning("Incomplete response detected, attempting to continue");
                    }
                }
            }
        }
        
        if (iteration >= _maxIterations)
        {
            _logger?.LogWarning("Reached maximum iterations ({Max}), stopping", _maxIterations);
            
            yield return new ConversationChunk
            {
                TextContent = "Maximum conversation iterations reached. Please start a new conversation if needed.",
                Type = ChunkType.Error,
                IsComplete = true
            };
        }
    }

    /// <summary>
    /// Process a message without streaming (legacy compatibility)
    /// </summary>
    public async Task<string> ProcessMessageAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var responseBuilder = new StringBuilder();
        
        await foreach (var chunk in ProcessStreamingAsync(userMessage, cancellationToken))
        {
            if (chunk.Type == ChunkType.Text && !string.IsNullOrWhiteSpace(chunk.TextContent))
            {
                responseBuilder.Append(chunk.TextContent);
            }
        }
        
        return responseBuilder.ToString();
    }

    /// <summary>
    /// Stream response from LLM
    /// </summary>
    private async IAsyncEnumerable<string> StreamLlmResponseAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_streamTimeout);
        
        IAsyncEnumerable<LlmStreamResponse> streamResponse;
        
        try
        {
            streamResponse = _llmClient.StreamCompleteAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger?.LogError("LLM streaming response timed out after {Timeout}", _streamTimeout);
            throw new TimeoutException($"LLM response timed out after {_streamTimeout}");
        }
        
        await foreach (var chunk in streamResponse)
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                yield return chunk.TextDelta;
            }
        }
    }

    /// <summary>
    /// Create LLM request with tool definitions
    /// </summary>
    private LlmRequest CreateRequestWithTools(List<ToolRegistration> tools)
    {
        var context = _contextManager.GetContext();
        var request = context.CreateRequest();
        
        // Set streaming parameters
        request.Temperature = 0.7f;
        request.MaxTokens = 2000;
        request.Stream = true;
        
        // Tool definitions are already in the system prompt from SystemPromptService
        // For Qwen models, we add specific formatting instructions
        if (_currentModel.Contains("qwen", StringComparison.OrdinalIgnoreCase) && tools.Any())
        {
            // Update system instruction with Qwen-specific tool call format
            context.SystemInstruction += 
                "\n\nWhen you need to use a tool, output it in the following format:\n" +
                "<tool_call>\n{\"name\": \"tool_name\", \"arguments\": {\"param1\": \"value1\"}}\n</tool_call>";
            
            // Recreate request with updated context
            request = context.CreateRequest();
            request.Temperature = 0.7f;
            request.MaxTokens = 2000;
            request.Stream = true;
        }
        
        return request;
    }


    /// <summary>
    /// Format tool results for the model
    /// </summary>
    private string FormatToolResults(List<ModelToolCall> toolCalls, List<string> results)
    {
        var formatted = new StringBuilder();
        formatted.AppendLine("Tool Execution Results:");
        formatted.AppendLine();
        
        for (int i = 0; i < Math.Min(toolCalls.Count, results.Count); i++)
        {
            formatted.AppendLine($"Tool: {toolCalls[i].ToolId}");
            formatted.AppendLine($"Result: {results[i]}");
            formatted.AppendLine();
        }
        
        return formatted.ToString();
    }

    /// <summary>
    /// Get conversation statistics
    /// </summary>
    public ConversationStats GetStats()
    {
        var contextStats = _contextManager.GetStats();
        return new ConversationStats
        {
            MessageCount = contextStats.MessageCount,
            TotalTokens = contextStats.EstimatedTokens,
            AccumulatorStats = _accumulator.GetStats(),
            Model = _currentModel,
            Provider = _currentProvider
        };
    }
}

/// <summary>
/// Conversation statistics
/// </summary>
public class ConversationStats
{
    public int MessageCount { get; set; }
    public int TotalTokens { get; set; }
    public AccumulatorStats AccumulatorStats { get; set; } = new();
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
}