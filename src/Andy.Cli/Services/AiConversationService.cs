using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Parsing;
using Andy.Cli.Parsing.Compiler;
using Andy.Cli.Parsing.Rendering;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// Main AI conversation service with tool integration
/// </summary>
public class AiConversationService
{
    private readonly LlmClient _llmClient;
    private readonly ToolExecutionService _toolService;
    private readonly ContextManager _contextManager;
    private readonly FeedView _feed;
    private readonly IToolRegistry _toolRegistry;
    private readonly LlmResponseCompiler _compiler;
    private readonly AstRenderer _renderer;
    private readonly ILogger<AiConversationService>? _logger;
    private string _currentModel = "";
    private string _currentProvider = "";

    public AiConversationService(
        LlmClient llmClient,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        string systemPrompt,
        IJsonRepairService jsonRepair,
        ILogger<AiConversationService>? logger = null,
        string modelName = "",
        string providerName = "")
    {
        _llmClient = llmClient;
        _toolRegistry = toolRegistry;
        _feed = feed;
        _logger = logger;
        _toolService = new ToolExecutionService(toolRegistry, toolExecutor, feed);
        _contextManager = new ContextManager(systemPrompt);
        _compiler = new LlmResponseCompiler(providerName, jsonRepair, null);
        _renderer = new AstRenderer(new RenderOptions 
        { 
            ShowToolCalls = false,
            UseEmoji = false,
            ToolCallFormat = ToolCallDisplayFormat.Hidden
        }, null);
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
    }

    /// <summary>
    /// Process a user message with tool support and streaming responses
    /// </summary>
    public async Task<string> ProcessMessageAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        return await ProcessMessageAsync(userMessage, enableStreaming: false, cancellationToken);
    }
    
    /// <summary>
    /// Process a user message with tool support
    /// </summary>
    public async Task<string> ProcessMessageAsync(
        string userMessage,
        bool enableStreaming,
        CancellationToken cancellationToken = default)
    {
        // Add user message to context
        _contextManager.AddUserMessage(userMessage);

        // Get available tools
        var availableTools = _toolRegistry.GetTools(enabledOnly: true).ToList();

        // Create the LLM request with tool definitions
        var request = CreateRequestWithTools(availableTools);

        // Track the conversation loop
        var maxIterations = 5; // Prevent infinite loops
        var iteration = 0;
        var finalResponse = new StringBuilder();

        while (iteration < maxIterations)
        {
            iteration++;
            
            // Get LLM response with streaming if enabled
            var response = enableStreaming 
                ? await GetLlmStreamingResponseAsync(request, cancellationToken)
                : await GetLlmResponseAsync(request, cancellationToken);
            
            if (response == null)
            {
                break;
            }

            // Parse the response using the AST compiler
            var compilerOptions = new CompilerOptions
            {
                ModelProvider = _currentProvider,
                ModelName = _currentModel,
                PreserveThoughts = false,
                EnableOptimizations = true
            };
            
            var compilationResult = _compiler.Compile(response.Content ?? "", compilerOptions);
            
            // Log any compilation diagnostics
            foreach (var diagnostic in compilationResult.Diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    _logger?.LogError("Compilation error: {Message}", diagnostic.Message);
                else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                    _logger?.LogWarning("Compilation warning: {Message}", diagnostic.Message);
            }
            
            if (!compilationResult.Success || compilationResult.Ast == null)
            {
                _logger?.LogWarning("Failed to parse response cleanly, falling back to basic parsing");
                
                // Fall back to basic parsing - still try to extract tool calls
                compilationResult.Ast = new ResponseNode
                {
                    ModelProvider = _currentProvider,
                    ModelName = _currentModel
                };
                
                // Add the raw text as a text node (will be cleaned by renderer)
                compilationResult.Ast.Children.Add(new TextNode
                {
                    Content = response.Content ?? "",
                    Format = TextFormat.Plain
                });
            }
            
            // Render the AST for display and tool extraction
            var renderResult = _renderer.RenderForStreaming(compilationResult.Ast);
            
            if (renderResult.ToolCalls.Any())
            {
                // Display text content if it exists
                if (!string.IsNullOrWhiteSpace(renderResult.TextContent))
                {
                    _feed.AddMarkdownRich(renderResult.TextContent);
                }
                
                // Generate call IDs for each tool call first
                var contextToolCalls = new List<ContextToolCall>();
                var callIdMap = new Dictionary<int, string>();
                
                for (int i = 0; i < renderResult.ToolCalls.Count; i++)
                {
                    var callId = "call_" + Guid.NewGuid().ToString("N");
                    callIdMap[i] = callId;
                    contextToolCalls.Add(new ContextToolCall
                    {
                        CallId = callId,
                        ToolId = renderResult.ToolCalls[i].ToolId,
                        Parameters = renderResult.ToolCalls[i].Parameters
                    });
                }
                
                // Add assistant message with tool calls to context
                // This is critical - the assistant message must include tool_calls
                _contextManager.AddAssistantMessage(renderResult.TextContent ?? "", contextToolCalls);
                
                // Execute tools and collect results
                var toolResults = new List<string>();
                
                for (int i = 0; i < renderResult.ToolCalls.Count; i++)
                {
                    var toolCall = renderResult.ToolCalls[i];
                    var callId = callIdMap[i];
                    
                    // Tool execution is displayed by ToolExecutionService
                    var result = await _toolService.ExecuteToolAsync(
                        toolCall.ToolId,
                        toolCall.Parameters,
                        cancellationToken);
                    
                    // Add to context - use Data which contains the properly serialized data
                    var outputStr = result.Data?.ToString() ?? result.ErrorMessage ?? "No output";
                    _contextManager.AddToolExecution(
                        toolCall.ToolId,
                        callId,
                        toolCall.Parameters,
                        outputStr);
                    
                    toolResults.Add(outputStr);
                }

                // Don't add tool results as user message - they're already in context as tool responses
                
                // Update request for next iteration
                request = _contextManager.GetContext().CreateRequest();
            }
            else
            {
                // Regular text response - rendered content without tool calls
                var content = renderResult.TextContent ?? "";
                
                // Check if the response looks like a raw tool execution result (escaped JSON)
                if (content.StartsWith("\"\\\"[Tool Execution:") || content.StartsWith("\"[Tool Execution:"))
                {
                    // Try to unescape and parse the content
                    try
                    {
                        // Remove outer quotes if present
                        if (content.StartsWith("\"") && content.EndsWith("\""))
                        {
                            content = content.Substring(1, content.Length - 2);
                        }
                        
                        // Unescape the content
                        content = content.Replace("\\n", "\n")
                                       .Replace("\\\"", "\"")
                                       .Replace("\\\\", "\\")
                                       .Replace("\\u0022", "\"");
                    }
                    catch
                    {
                        // If unescaping fails, use original content
                    }
                    
                    finalResponse.AppendLine(content);
                    _contextManager.AddAssistantMessage(content);
                    
                    // Display the cleaned response
                    if (!string.IsNullOrEmpty(content))
                    {
                        _feed.AddMarkdownRich(content);
                    }
                }
                else
                {
                    finalResponse.AppendLine(content);
                    _contextManager.AddAssistantMessage(content);
                    
                    // Display the cleaned response
                    if (!string.IsNullOrEmpty(content))
                    {
                        _feed.AddMarkdownRich(content);
                    }
                }
                
                break; // No more tool calls, we're done
            }
        }

        // Show context stats
        var stats = _contextManager.GetStats();
        _feed.AddMarkdownRich($"*Context: {stats.MessageCount} messages, ~{stats.EstimatedTokens} tokens, {stats.ToolCallCount} tool calls*");

        return finalResponse.ToString();
    }

    /// <summary>
    /// Create LLM request with tool definitions
    /// </summary>
    private LlmRequest CreateRequestWithTools(List<ToolRegistration> tools)
    {
        var context = _contextManager.GetContext();
        var request = context.CreateRequest();
        
        // Tool definitions are already in the system prompt from SystemPromptService
        // Don't add them again to avoid duplication
        
        return request;
    }

    // Removed BuildToolPrompt - tool definitions are in system prompt

    /// <summary>
    /// Get LLM response with streaming support
    /// </summary>
    private async Task<LlmResponse?> GetLlmResponseAsync(
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _llmClient.CompleteAsync(request, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            _feed.AddMarkdownRich($"**LLM Error:** {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Get LLM response with streaming enabled
    /// </summary>
    private async Task<LlmResponse?> GetLlmStreamingResponseAsync(
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullContent = new StringBuilder();
            var isComplete = false;
            string? finishReason = null;
            var functionCalls = new List<FunctionCall>();
            
            // Accumulate the entire response during streaming
            // For Qwen models, tool calls are only valid when streaming is complete
            await foreach (var chunk in _llmClient.StreamCompleteAsync(request, cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    fullContent.Append(chunk.TextDelta);
                }
                
                // Accumulate function calls
                if (chunk.FunctionCall != null)
                {
                    functionCalls.Add(chunk.FunctionCall);
                }
                
                // Check if streaming is complete
                if (chunk.IsComplete)
                {
                    isComplete = true;
                    finishReason = "stop";  // Set a default finish reason
                }
            }
            
            var content = fullContent.ToString();
            
            // Debug: Show raw LLM output (can be controlled by environment variable)
            if (Environment.GetEnvironmentVariable("ANDY_DEBUG_RAW") == "true" && !string.IsNullOrWhiteSpace(content))
            {
                _feed.AddMarkdownRich("```raw-llm-output");
                _feed.AddMarkdownRich(content);
                _feed.AddMarkdownRich("```");
            }
            
            // Only process tool calls if streaming is complete
            // This matches qwen-code's behavior of waiting for finish_reason
            if (!isComplete)
            {
                _feed.AddMarkdownRich("**Warning:** Streaming did not complete properly");
            }
            
            // Return the raw content for processing in the main loop
            return new LlmResponse
            {
                Content = content,
                FinishReason = finishReason
            };
        }
        catch (Exception ex)
        {
            _feed.AddMarkdownRich($"**LLM Error:** {ex.Message}");
            return null;
        }
    }

    // Tool call extraction now handled by AST compiler

    // Tool result formatting and text extraction now handled by AST compiler

    /// <summary>
    /// Update the system prompt
    /// </summary>
    public void UpdateSystemPrompt(string prompt)
    {
        _contextManager.UpdateSystemPrompt(prompt);
    }

    /// <summary>
    /// Set the current model and provider for response interpretation
    /// </summary>
    public void SetModelInfo(string modelName, string providerName)
    {
        _currentModel = modelName ?? "";
        _currentProvider = providerName ?? "";
    }

    /// <summary>
    /// Clear the conversation context
    /// </summary>
    public void ClearContext()
    {
        _contextManager.Clear();
    }

    /// <summary>
    /// Get context statistics
    /// </summary>
    public ContextStats GetContextStats()
    {
        return _contextManager.GetStats();
    }
    
    /// <summary>
    /// Internal tool call representation
    /// </summary>
    private class ToolCall
    {
        public string ToolId { get; set; } = "";
        public Dictionary<string, object?> Parameters { get; set; } = new();
    }
}