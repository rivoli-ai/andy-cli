using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Andy.Tools.Execution;

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
    private readonly IQwenResponseParser _parser;
    private readonly IToolCallValidator _validator;
    private string _currentModel = "";
    private string _currentProvider = "";

    public AiConversationService(
        LlmClient llmClient,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        string systemPrompt,
        IQwenResponseParser parser,
        IToolCallValidator validator,
        string modelName = "",
        string providerName = "")
    {
        _llmClient = llmClient;
        _toolRegistry = toolRegistry;
        _feed = feed;
        _toolService = new ToolExecutionService(toolRegistry, toolExecutor, feed);
        _contextManager = new ContextManager(systemPrompt);
        _parser = parser;
        _validator = validator;
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

            // Check if response contains tool calls
            var toolCalls = ExtractToolCalls(response);
            
            if (toolCalls.Any())
            {
                // Clean and display the response text (without tool call artifacts)
                var cleanedContent = _parser.CleanResponseText(response.Content);
                if (!string.IsNullOrWhiteSpace(cleanedContent))
                {
                    _feed.AddMarkdownRich(cleanedContent);
                }
                
                // Execute tools and collect results
                var toolResults = new List<string>();
                
                foreach (var toolCall in toolCalls)
                {
                    // Tool execution is displayed by ToolExecutionService
                    var result = await _toolService.ExecuteToolAsync(
                        toolCall.ToolId,
                        toolCall.Parameters,
                        cancellationToken);
                    
                    // Add to context - use Data which contains the properly serialized data
                    var outputStr = result.Data?.ToString() ?? result.ErrorMessage ?? "No output";
                    _contextManager.AddToolExecution(
                        toolCall.ToolId,
                        toolCall.Parameters,
                        outputStr);
                    
                    toolResults.Add(outputStr);
                }

                // Continue conversation with tool results
                var toolResultMessage = FormatToolResults(toolCalls, toolResults);
                _contextManager.AddUserMessage($"[Tool Results]\n{toolResultMessage}");
                
                // Update request for next iteration
                request = _contextManager.GetContext().CreateRequest();
            }
            else
            {
                // Regular text response
                var content = response.Content ?? "";
                
                // Debug: Show raw LLM output (can be controlled by environment variable)
                if (Environment.GetEnvironmentVariable("ANDY_DEBUG_RAW") == "true" && !string.IsNullOrWhiteSpace(content))
                {
                    _feed.AddMarkdownRich("```raw-llm-output");
                    _feed.AddMarkdownRich(content);
                    _feed.AddMarkdownRich("```");
                }
                
                // Clean the response using the model-specific interpreter
                content = _parser.CleanResponseText(content);
                
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
            
            // Don't display content during streaming - just accumulate it
            // This prevents duplication when we display the cleaned content later
            await foreach (var chunk in _llmClient.StreamCompleteAsync(request, cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    fullContent.Append(chunk.TextDelta);
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
            
            // The main ProcessMessageAsync will handle displaying the cleaned content
            
            // Return the raw content for processing in the main loop
            return new LlmResponse
            {
                Content = content
            };
        }
        catch (Exception ex)
        {
            _feed.AddMarkdownRich($"**LLM Error:** {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract tool calls from LLM response
    /// </summary>
    private List<ToolCall> ExtractToolCalls(LlmResponse response)
    {
        if (string.IsNullOrEmpty(response.Content))
        {
            return new List<ToolCall>();
        }

        // Use the model-specific interpreter
        var parsedResponse = _parser.Parse(response.Content);
        var modelToolCalls = parsedResponse.ToolCalls;
        
        // Convert from interpreter's ModelToolCall to our internal ToolCall format
        var result = new List<ToolCall>();
        foreach (var call in modelToolCalls)
        {
            result.Add(new ToolCall
            {
                ToolId = call.ToolId,
                Parameters = call.Parameters
            });
        }
        
        return result;
    }
    
    // Note: ExtractWrappedToolCalls removed - now handled by ModelResponseInterpreter
    
    // Note: ExtractDirectJsonToolCalls removed - now handled by ModelResponseInterpreter
    
    // Note: ParseToolCallJson and ParseParameters removed - now handled by ModelResponseInterpreter

    /// <summary>
    /// Format tool results for LLM
    /// </summary>
    private string FormatToolResults(List<ToolCall> toolCalls, List<string> results)
    {
        // Convert our internal ToolCall to ModelToolCall for the interpreter
        var modelToolCalls = toolCalls.Select(tc => new ModelToolCall 
        { 
            ToolId = tc.ToolId, 
            Parameters = tc.Parameters 
        }).ToList();
        
        // Use the model-specific interpreter for formatting
        // Format the tool results for the model
        var sb = new StringBuilder();
        foreach (var result in results)
        {
            sb.AppendLine($"Tool result: {result}");
        }
        return sb.ToString();
    }
    
    // Note: FindJsonEnd removed - no longer needed with ModelResponseInterpreter
    
    /// <summary>
    /// Extract any text that appears before tool calls in the response
    /// </summary>
    private string? ExtractTextBeforeToolCall(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;
        
        // Find the start of JSON tool call
        var jsonStart = content.IndexOf('{');
        if (jsonStart > 0)
        {
            var beforeText = content.Substring(0, jsonStart).Trim();
            // Filter out markdown code block markers
            beforeText = beforeText.Replace("```json", "").Replace("```", "").Trim();
            return string.IsNullOrWhiteSpace(beforeText) ? null : beforeText;
        }
        
        // Find the start of wrapped tool call
        var toolUseStart = content.IndexOf("<tool_use>", StringComparison.OrdinalIgnoreCase);
        if (toolUseStart > 0)
        {
            return content.Substring(0, toolUseStart).Trim();
        }
        
        return null;
    }

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
}