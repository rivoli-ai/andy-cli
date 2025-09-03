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

    public AiConversationService(
        LlmClient llmClient,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        string systemPrompt)
    {
        _llmClient = llmClient;
        _toolRegistry = toolRegistry;
        _feed = feed;
        _toolService = new ToolExecutionService(toolRegistry, toolExecutor, feed);
        _contextManager = new ContextManager(systemPrompt);
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
                // Execute tools and collect results
                var toolResults = new List<string>();
                
                foreach (var toolCall in toolCalls)
                {
                    _feed.AddMarkdownRich($"**Calling tool:** `{toolCall.ToolId}`");
                    
                    var result = await _toolService.ExecuteToolAsync(
                        toolCall.ToolId,
                        toolCall.Parameters,
                        cancellationToken);
                    
                    // Add to context
                    var outputStr = result.Output?.ToString() ?? result.Error ?? "No output";
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
                finalResponse.AppendLine(response.Content);
                _contextManager.AddAssistantMessage(response.Content ?? "");
                
                // Display the response
                if (!string.IsNullOrEmpty(response.Content))
                {
                    _feed.AddMarkdownRich(response.Content);
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

        // Add tool definitions to the request
        // This format will depend on your LLM provider
        // For now, we'll add them to the system prompt
        if (tools.Any())
        {
            var toolPrompt = BuildToolPrompt(tools);
            request.SystemPrompt = $"{request.SystemPrompt}\n\n{toolPrompt}";
        }

        return request;
    }

    /// <summary>
    /// Build tool usage prompt
    /// </summary>
    private string BuildToolPrompt(List<ToolRegistration> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You have access to the following tools:");
        sb.AppendLine();

        foreach (var tool in tools)
        {
            sb.AppendLine($"Tool: {tool.Metadata.Id}");
            sb.AppendLine($"Description: {tool.Metadata.Description}");
            
            if (tool.Metadata.Parameters.Any())
            {
                sb.AppendLine("Parameters:");
                foreach (var param in tool.Metadata.Parameters)
                {
                    var required = param.Required ? "required" : "optional";
                    sb.AppendLine($"  - {param.Name} ({param.Type}, {required}): {param.Description}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("To use a tool, respond with:");
        sb.AppendLine("<tool_use>");
        sb.AppendLine("{");
        sb.AppendLine("  \"tool\": \"tool_id\",");
        sb.AppendLine("  \"parameters\": {");
        sb.AppendLine("    \"param_name\": \"value\"");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("</tool_use>");
        sb.AppendLine();
        sb.AppendLine("You can use multiple tools in sequence. After receiving tool results, continue with your response.");

        return sb.ToString();
    }

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
            var streamingMessage = _feed.AddStreamingMessage();
            
            await foreach (var chunk in _llmClient.StreamCompleteAsync(request, cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    fullContent.Append(chunk.TextDelta);
                    streamingMessage.AppendContent(chunk.TextDelta);
                }
            }
            
            streamingMessage.Complete();
            
            // Return the complete response
            return new LlmResponse
            {
                Content = fullContent.ToString()
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
        var toolCalls = new List<ToolCall>();
        
        if (string.IsNullOrEmpty(response.Content))
        {
            return toolCalls;
        }

        // Look for tool_use blocks
        var content = response.Content;
        var startTag = "<tool_use>";
        var endTag = "</tool_use>";
        
        var startIndex = 0;
        while ((startIndex = content.IndexOf(startTag, startIndex)) != -1)
        {
            var endIndex = content.IndexOf(endTag, startIndex);
            if (endIndex == -1) break;
            
            var toolJson = content.Substring(
                startIndex + startTag.Length,
                endIndex - startIndex - startTag.Length).Trim();
            
            try
            {
                // Parse JSON
                using var doc = JsonDocument.Parse(toolJson);
                var root = doc.RootElement;
                
                var toolCall = new ToolCall
                {
                    ToolId = root.GetProperty("tool").GetString() ?? "",
                    Parameters = new Dictionary<string, object?>()
                };
                
                if (root.TryGetProperty("parameters", out var parameters))
                {
                    foreach (var param in parameters.EnumerateObject())
                    {
                        toolCall.Parameters[param.Name] = param.Value.ValueKind switch
                        {
                            JsonValueKind.String => param.Value.GetString(),
                            JsonValueKind.Number => param.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            _ => param.Value.GetRawText()
                        };
                    }
                }
                
                toolCalls.Add(toolCall);
            }
            catch (JsonException ex)
            {
                _feed.AddMarkdownRich($"*Failed to parse tool call: {ex.Message}*");
            }
            
            startIndex = endIndex + endTag.Length;
        }

        return toolCalls;
    }

    /// <summary>
    /// Format tool results for LLM
    /// </summary>
    private string FormatToolResults(List<ToolCall> toolCalls, List<string> results)
    {
        var sb = new StringBuilder();
        
        for (int i = 0; i < toolCalls.Count; i++)
        {
            sb.AppendLine($"Tool: {toolCalls[i].ToolId}");
            sb.AppendLine($"Result: {results[i]}");
            sb.AppendLine();
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Update the system prompt
    /// </summary>
    public void UpdateSystemPrompt(string prompt)
    {
        _contextManager.UpdateSystemPrompt(prompt);
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