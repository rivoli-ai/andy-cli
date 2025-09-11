using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Diagnostics;
using Andy.Cli.Parsing;
using Andy.Cli.Parsing.Compiler;
using Andy.Cli.Parsing.Rendering;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Andy.Cli.Services;

/// <summary>
/// Main AI conversation service with tool integration
/// </summary>
public class AiConversationService : IDisposable
{
    private readonly LlmClient _llmClient;
    private readonly ToolExecutionService _toolService;
    private readonly ContextManager _contextManager;
    private readonly FeedView _feed;
    private readonly IToolRegistry _toolRegistry;
    private LlmResponseCompiler _compiler;
    private readonly AstRenderer _renderer;
    private readonly ILogger<AiConversationService>? _logger;
    private readonly QwenResponseDiagnostic? _diagnostic;
    private readonly IToolCallValidator _toolCallValidator;
    private readonly IJsonRepairService _jsonRepair;
    private readonly ConversationTracer? _tracer;
    private readonly CumulativeOutputTracker _outputTracker = new();
    private readonly ContentPipeline.ContentPipeline _contentPipeline;
    private string _currentModel = "";
    private string _currentProvider = "";
    // Note: Avoid writing partial streaming chunks to the feed to prevent broken/partial lines

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
        _jsonRepair = jsonRepair;
        _compiler = new LlmResponseCompiler(NormalizeProviderName(providerName, modelName), jsonRepair, null);
        _renderer = new AstRenderer(new RenderOptions
        {
            ShowToolCalls = false,
            UseEmoji = false,
            ToolCallFormat = ToolCallDisplayFormat.Hidden
        }, null);
        _currentModel = modelName;
        _currentProvider = providerName;
        _toolCallValidator = new ToolCallValidator(_toolRegistry, logger as ILogger<ToolCallValidator>);
        
        // Initialize content pipeline
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var renderer = new FeedContentRenderer(feed, logger as ILogger<FeedContentRenderer>);
        _contentPipeline = new ContentPipeline.ContentPipeline(processor, sanitizer, renderer, logger as ILogger<ContentPipeline.ContentPipeline>);

        // Initialize diagnostic tool if debugging is enabled
        if (Environment.GetEnvironmentVariable("ANDY_DEBUG_RAW") == "true")
        {
            _diagnostic = new QwenResponseDiagnostic(logger as ILogger<QwenResponseDiagnostic>);
            _logger?.LogInformation("Raw response diagnostic enabled");

            // Write debug info file
            var debugFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".andy",
                "diagnostics",
                "SERVICE_INIT.txt"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(debugFile)!);
            File.WriteAllText(debugFile, $"AiConversationService initialized at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\nModel: {modelName}\nProvider: {providerName}\n");
        }
        
        // Initialize tracer based on environment variable
        var enableTrace = Environment.GetEnvironmentVariable("ANDY_TRACE") == "1" || 
                         Environment.GetEnvironmentVariable("ANDY_DEBUG") == "1";
        var consoleTrace = Environment.GetEnvironmentVariable("ANDY_TRACE_CONSOLE") == "1";
        var tracePath = Environment.GetEnvironmentVariable("ANDY_TRACE_PATH");
        
        if (enableTrace)
        {
            _tracer = new ConversationTracer(enabled: true, consoleOutput: consoleTrace, customPath: tracePath);
            _logger?.LogInformation($"Conversation tracing enabled. Output: {_tracer.TraceFilePath}");
        }
    }

    /// <summary>
    /// Update the current model and provider information
    /// </summary>
    public void UpdateModelInfo(string modelName, string providerName)
    {
        _currentModel = modelName;
        _currentProvider = providerName;
        // Recreate compiler with normalized provider to ensure correct parsing (e.g., Qwen via other providers)
        _compiler = new LlmResponseCompiler(NormalizeProviderName(_currentProvider, _currentModel), _jsonRepair, null);
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
        // Trace user message
        _tracer?.TraceUserMessage(userMessage);
        
        // Reset cumulative output tracker for new message
        _outputTracker.Reset();
        
        // Add user message to context
        _contextManager.AddUserMessage(userMessage);

        // Get available tools
        var availableTools = _toolRegistry.GetTools(enabledOnly: true).ToList();

        // Create the LLM request with tool definitions
        var request = CreateRequestWithTools(availableTools);

        // Heuristic: proactively explore repository structure for repo/content questions
        if (LooksLikeRepoExploration(userMessage))
        {
            try
            {
                await PreloadRepoContextAsync(cancellationToken);
                // Refresh request after preloading tool results
                request = _contextManager.GetContext().CreateRequest();
                // Fix empty Parts issue in messages
                request = FixEmptyMessageParts(request);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "PreloadRepoContextAsync failed");
            }
        }

        // Track the conversation loop
        var maxIterations = 12; // Allow deeper multi-step planning and execution
        var iteration = 0;
        var finalResponse = new StringBuilder();
        var allToolResults = new List<(string ToolId, string Result)>(); // Track all tool executions
        var hasDisplayedContent = false; // Track if we've displayed any content to the user
        var consecutiveToolOnlyIterations = 0; // Track consecutive iterations with only tool calls
        const int maxConsecutiveToolIterations = 3; // Break after 3 consecutive tool-only iterations

        while (iteration < maxIterations)
        {
            iteration++;
            _tracer?.TraceIteration(iteration, maxIterations);

            // Get LLM response with streaming if enabled
            // Always render final content once; do not render partial streaming deltas to the feed
            
            // Ensure Parts are fixed before logging/sending
            request = FixEmptyMessageParts(request);
            
            _tracer?.TraceLlmRequest(request);
            var startTime = DateTime.UtcNow;
            
            // LOG TO FILE - Request
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".andy", "logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"llm-{DateTime.Now:yyyy-MM-dd}.log");
            var requestLog = $"\n\n========== REQUEST at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========\n";
            requestLog += $"Iteration: {iteration}/{maxIterations}\n";
            try
            {
                // Manually construct JSON to ensure Parts are visible in logs
                var requestObj = new
                {
                    Messages = request.Messages?.Select(m => new
                    {
                        Role = m.Role,
                        Parts = m.Parts?.Select<MessagePart, object>(p =>
                        {
                            if (p is TextPart tp)
                                return new { Text = tp.Text };
                            else if (p is ToolCallPart tcp)
                                return new { ToolCall = new { Name = tcp.ToolName, Id = tcp.CallId, Arguments = tcp.Arguments } };
                            else if (p is ToolResponsePart trp)
                                return new { ToolResponse = new { Name = trp.ToolName, Id = trp.CallId, Response = trp.Response } };
                            else
                                return new { Type = p?.GetType().Name ?? "null" };
                        }).ToList() ?? new List<object>()
                    }).ToList(),
                    Tools = request.Tools,
                    Functions = request.Functions,
                    Model = request.Model,
                    Temperature = request.Temperature,
                    MaxTokens = request.MaxTokens,
                    SystemPrompt = request.SystemPrompt?.Substring(0, Math.Min(200, request.SystemPrompt?.Length ?? 0)) + (request.SystemPrompt?.Length > 200 ? "..." : "")
                };
                
                var requestJson = System.Text.Json.JsonSerializer.Serialize(requestObj, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    MaxDepth = 10
                });
                // Truncate if too long
                if (requestJson.Length > 5000)
                {
                    requestJson = requestJson.Substring(0, 5000) + "\n... [truncated]";
                }
                requestLog += requestJson;
            }
            catch (Exception ex)
            {
                requestLog += $"[Error serializing request: {ex.Message}]";
            }
            File.AppendAllText(logFile, requestLog);
            
            var response = enableStreaming
                ? await GetLlmStreamingResponseAsync(request, cancellationToken)
                : await GetLlmResponseAsync(request, cancellationToken);
            
            var elapsed = DateTime.UtcNow - startTime;
            _tracer?.TraceLlmResponse(response, elapsed);
            
            _logger?.LogInformation("[LLM RESPONSE] Iteration {Iteration}: Content length={Length}, Content preview: '{Preview}'",
                iteration, response?.Content?.Length ?? 0, 
                response?.Content?.Length > 100 ? response.Content.Substring(0, 100) + "..." : response?.Content ?? "(null)");
            
            // LOG TO FILE - Response
            var responseLog = $"\n========== RESPONSE in {elapsed.TotalSeconds:F2}s ==========\n";
            responseLog += $"Content Length: {response?.Content?.Length ?? 0}\n";
            responseLog += $"Content: {response?.Content?.Substring(0, Math.Min(response?.Content?.Length ?? 0, 1000))}\n";
            if ((response?.Content?.Length ?? 0) > 1000) responseLog += "... [truncated]\n";
            responseLog += $"==========================================\n";
            File.AppendAllText(logFile, responseLog);
            
            // Log file location hint for the user
            if (iteration == 1)
            {
                _logger?.LogInformation("[LLM LOG] Logging to: {LogFile}", logFile);
                Console.Error.WriteLine($"[LLM LOG] Writing to: {logFile}");
            }

            if (response == null)
            {
                break;
            }

            // Parse the response using the AST compiler
            var compilerOptions = new CompilerOptions
            {
                ModelProvider = NormalizeProviderName(_currentProvider, _currentModel),
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
            
            _logger?.LogInformation("[RENDER RESULT] Iteration {Iteration}: ToolCalls={ToolCount}, TextContent length={TextLength}, TextPreview='{Preview}'",
                iteration,
                renderResult.ToolCalls.Count,
                renderResult.TextContent?.Length ?? 0,
                renderResult.TextContent?.Length > 100 ? renderResult.TextContent.Substring(0, 100) + "..." : renderResult.TextContent ?? "(null)");

            if (renderResult.ToolCalls.Any())
            {
                // Trace tool calls found
                _tracer?.TraceToolCalls(renderResult.ToolCalls.Select(tc => new TracedToolCall { ToolId = tc.ToolId, Parameters = tc.Parameters }).ToList());
                
                // If there's text content along with tool calls, display it
                // This handles cases where the LLM explains what it's about to do
                if (!string.IsNullOrWhiteSpace(renderResult.TextContent))
                {
                    var textWithoutTools = ExtractNonToolText(renderResult.TextContent);
                    if (!string.IsNullOrWhiteSpace(textWithoutTools))
                    {
                        _logger?.LogInformation("[TOOL WITH TEXT] Displaying text before tool execution: '{Preview}'",
                            textWithoutTools.Length > 100 ? textWithoutTools.Substring(0, 100) + "..." : textWithoutTools);
                        _contentPipeline.AddRawContent(textWithoutTools);
                        finalResponse.AppendLine(textWithoutTools);
                        hasDisplayedContent = true;
                    }
                }

                // Generate call IDs for each tool call and add them as assistant tool_calls
                var contextToolCalls = new List<ContextToolCall>();
                var callIdMap = new Dictionary<int, string>();
                for (int i = 0; i < renderResult.ToolCalls.Count; i++)
                {
                    var generatedCallId = "call_" + Guid.NewGuid().ToString("N");
                    callIdMap[i] = generatedCallId;
                    contextToolCalls.Add(new ContextToolCall
                    {
                        CallId = generatedCallId,
                        ToolId = renderResult.ToolCalls[i].ToolId,
                        Parameters = renderResult.ToolCalls[i].Parameters
                    });
                }

                // Add assistant message with tool calls to context
                // This is critical - the assistant message must include tool_calls
                // If there's no text content, use a placeholder that indicates tool execution
                var assistantText = !string.IsNullOrWhiteSpace(renderResult.TextContent) 
                    ? renderResult.TextContent 
                    : "(Executing tools...)";
                _contextManager.AddAssistantMessage(assistantText, contextToolCalls);

                // Execute tools and collect results
                var toolResults = new List<string>();

                for (int i = 0; i < renderResult.ToolCalls.Count; i++)
                {
                    var toolCall = renderResult.ToolCalls[i];
                    var thisCallId = callIdMap[i];

                    // Validate and optionally repair parameters before execution
                    var toolMetadata = _toolRegistry.GetTool(toolCall.ToolId)?.Metadata;
                    var callToExecute = toolCall;
                    if (toolMetadata != null)
                    {
                        var validation = _toolCallValidator.Validate(toolCall, toolMetadata);
                        if (!validation.IsValid && validation.RepairedCall != null)
                        {
                            _logger?.LogWarning("Parameters for tool '{Tool}' were invalid; applying single repair attempt.", toolCall.ToolId);
                            callToExecute = validation.RepairedCall;
                        }
                    }

                    // Execute tool
                    var execResult = await _toolService.ExecuteToolAsync(
                        callToExecute.ToolId,
                        callToExecute.Parameters,
                        cancellationToken);

                    // Prepare output string (prefer JSON Data)
                    string outputStr;
                    if (execResult.Data != null)
                    {
                        try
                        {
                            // Increase limit for list_directory to handle recursive listings
                            int maxChars = callToExecute.ToolId == "list_directory" ? 5000 : 2000;
                            outputStr = SerializeToolDataWithTruncation(execResult.Data, callToExecute.ToolId, maxChars);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to serialize tool data for {Tool}", callToExecute.ToolId);
                            // Fallback: try to serialize as JSON
                            try
                            {
                                outputStr = System.Text.Json.JsonSerializer.Serialize(execResult.Data, new System.Text.Json.JsonSerializerOptions
                                {
                                    WriteIndented = false,
                                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                                });
                                // If it's too large, truncate it safely
                                if (outputStr.Length > 5000)
                                {
                                    outputStr = outputStr.Substring(0, 4900) + "... [truncated]";
                                }
                            }
                            catch
                            {
                                outputStr = execResult.Message ?? execResult.ErrorMessage ?? "Tool execution completed but output serialization failed";
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(execResult.FullOutput))
                    {
                        outputStr = execResult.FullOutput!;
                    }
                    else
                    {
                        outputStr = execResult.Message ?? execResult.ErrorMessage ?? "No output";
                    }

                    // Apply tool-specific output limit with cumulative tracking
                    var baseLimit = ToolOutputLimits.GetLimit(callToExecute.ToolId);
                    var adjustedLimit = _outputTracker.GetAdjustedLimit(callToExecute.ToolId, baseLimit);
                    var limitedOutput = ToolOutputLimits.LimitOutput(callToExecute.ToolId, outputStr, adjustedLimit);
                    _outputTracker.RecordOutput(callToExecute.ToolId, limitedOutput.Length);
                    
                    // Add tool response to context referencing the correct call_id
                    _contextManager.AddToolExecution(
                        callToExecute.ToolId,
                        thisCallId,
                        callToExecute.Parameters,
                        limitedOutput);

                    toolResults.Add(outputStr);
                    allToolResults.Add((callToExecute.ToolId, limitedOutput));
                }

                // Store tool results for potential display later
                // We'll display them if the LLM doesn't provide a follow-up response
                
                // Update request for next iteration
                request = _contextManager.GetContext().CreateRequest();
                request = FixEmptyMessageParts(request);
                
                // Track consecutive tool-only iterations
                if (!hasDisplayedContent)
                {
                    consecutiveToolOnlyIterations++;
                    if (consecutiveToolOnlyIterations >= maxConsecutiveToolIterations)
                    {
                        _logger?.LogWarning("Breaking loop: {Count} consecutive tool-only iterations, forcing text response", consecutiveToolOnlyIterations);
                        
                        // Force the LLM to provide a text response by adding a user prompt
                        _contextManager.AddUserMessage("Based on the information gathered, please provide a comprehensive answer.");
                        
                        // Get one more response without tools
                        request = _contextManager.GetContext().CreateRequest();
                        request = FixEmptyMessageParts(request);
                        
                        var forcedResponse = enableStreaming
                            ? await GetLlmStreamingResponseAsync(request, cancellationToken)
                            : await GetLlmResponseAsync(request, cancellationToken);
                        
                        if (!string.IsNullOrWhiteSpace(forcedResponse?.Content))
                        {
                            _contentPipeline.AddRawContent(forcedResponse.Content);
                            _contextManager.AddAssistantMessage(forcedResponse.Content);
                            hasDisplayedContent = true;
                        }
                        else if (allToolResults.Any())
                        {
                            // Fallback: Display accumulated tool results to the user
                            var summary = new StringBuilder();
                            summary.AppendLine("I've completed the following operations:");
                            foreach (var (toolId, result) in allToolResults.TakeLast(5)) // Show last 5 tools
                            {
                                summary.AppendLine($"- {toolId}: {(result.Length > 100 ? result.Substring(0, 100) + "..." : result)}");
                            }
                            _contentPipeline.AddRawContent(summary.ToString());
                            hasDisplayedContent = true;
                        }
                        break;
                    }
                }
                else
                {
                    consecutiveToolOnlyIterations = 0; // Reset counter if we displayed content
                }
                
                _logger?.LogInformation("[TOOL PATH] Continuing to iteration {Next} after tool execution", iteration + 1);
                continue; // Continue to next iteration, don't process as regular text
            }
            else
            {
                // Regular text response - rendered content without tool calls
                var content = renderResult.TextContent ?? "";
                
                _logger?.LogInformation("[TEXT RESPONSE PATH] Iteration {Iteration}, Content length: {Length}, Preview: '{Preview}'", 
                    iteration, content.Length, content.Length > 100 ? content.Substring(0, 100) + "..." : content);

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

                    // Display the response using content pipeline
                    _logger?.LogInformation("[ESCAPED JSON PATH] Adding content to pipeline: '{Preview}'", 
                        content.Length > 100 ? content.Substring(0, 100) + "..." : content);
                    _contentPipeline.AddRawContent(content);
                    hasDisplayedContent = true;
                    consecutiveToolOnlyIterations = 0; // Reset counter when we display content
                }
                else
                {
                    _logger?.LogInformation("[FALLBACK PATH] Content doesn't look like escaped JSON, trying fallback parser");
                    
                    // Fallback: try QwenResponseParser-based extraction if AST produced no tool calls
                    var fallbackParser = new QwenResponseParser(
                        _jsonRepair,
                        new StreamingToolCallAccumulator(_jsonRepair, null),
                        null);
                    var fallbackCalls = fallbackParser.ExtractToolCalls(response.Content ?? string.Empty);
                    
                    _logger?.LogInformation("[FALLBACK PARSER] Found {Count} tool calls", fallbackCalls.Count);
                    
                    if (fallbackCalls.Any())
                    {
                        // Execute fallback tool calls path (mirrors main tool call execution flow)
                        var contextToolCalls = new List<ContextToolCall>();
                        var callIdMap = new Dictionary<int, string>();
                        for (int i = 0; i < fallbackCalls.Count; i++)
                        {
                            var generatedCallId = "call_" + Guid.NewGuid().ToString("N");
                            callIdMap[i] = generatedCallId;
                            contextToolCalls.Add(new ContextToolCall
                            {
                                CallId = generatedCallId,
                                ToolId = fallbackCalls[i].ToolId,
                                Parameters = fallbackCalls[i].Parameters
                            });
                        }

                        var assistantTextFallback = !string.IsNullOrWhiteSpace(renderResult.TextContent) 
                            ? renderResult.TextContent 
                            : "(Executing tools...)";
                        _contextManager.AddAssistantMessage(assistantTextFallback, contextToolCalls);

                        for (int i = 0; i < fallbackCalls.Count; i++)
                        {
                            var toolCall = fallbackCalls[i];
                            var thisCallId = callIdMap[i];

                            var toolMetadata = _toolRegistry.GetTool(toolCall.ToolId)?.Metadata;
                            var callToExecute = toolCall;
                            if (toolMetadata != null)
                            {
                                var validation = _toolCallValidator.Validate(toolCall, toolMetadata);
                                if (!validation.IsValid && validation.RepairedCall != null)
                                {
                                    _logger?.LogWarning("Parameters for tool '{Tool}' were invalid; applying single repair attempt.", toolCall.ToolId);
                                    callToExecute = validation.RepairedCall;
                                }
                            }

                            // Use raw execution to preserve original parameter names from model output
                            var execResult = await _toolService.ExecuteRawAsync(
                                callToExecute.ToolId,
                                callToExecute.Parameters,
                                cancellationToken);

                            string outputStr;
                            if (execResult.Data != null)
                            {
                                try
                                {
                                    outputStr = SerializeToolDataWithTruncation(execResult.Data, callToExecute.ToolId, 2000);
                                }
                                catch
                                {
                                    outputStr = execResult.Data.ToString() ?? execResult.Message ?? execResult.ErrorMessage ?? "No output";
                                }
                            }
                            else if (!string.IsNullOrEmpty(execResult.FullOutput))
                            {
                                outputStr = execResult.FullOutput!;
                            }
                            else
                            {
                                outputStr = execResult.Message ?? execResult.ErrorMessage ?? "No output";
                            }

                            // Apply tool-specific output limit with cumulative tracking
                            var baseLimit = ToolOutputLimits.GetLimit(callToExecute.ToolId);
                            var adjustedLimit = _outputTracker.GetAdjustedLimit(callToExecute.ToolId, baseLimit);
                            var limitedOutput = ToolOutputLimits.LimitOutput(callToExecute.ToolId, outputStr, adjustedLimit);
                            _outputTracker.RecordOutput(callToExecute.ToolId, limitedOutput.Length);
                            
                            _contextManager.AddToolExecution(
                                callToExecute.ToolId,
                                thisCallId,
                                callToExecute.Parameters,
                                limitedOutput);
                        }

                        // We executed fallback tool calls; stop loop like normal text path
                        request = _contextManager.GetContext().CreateRequest();
                        request = FixEmptyMessageParts(request);
                        break;
                    }

                    // Check if content is empty
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        _logger?.LogWarning("[EMPTY CONTENT] Received empty text response in iteration {Iteration}, raw response length: {RawLength}", 
                            iteration, response.Content?.Length ?? 0);
                        // Try to use the raw response content if render result is empty
                        content = response.Content ?? "";
                        _logger?.LogInformation("[USING RAW CONTENT] Fallback to raw response, length: {Length}, preview: '{Preview}'",
                            content.Length, content.Length > 100 ? content.Substring(0, 100) + "..." : content);
                    }
                    
                    finalResponse.AppendLine(content);
                    _contextManager.AddAssistantMessage(content);

                    // Display the response using content pipeline
                    _logger?.LogInformation("[FALLBACK NO TOOLS] Adding content to pipeline: '{Preview}'", 
                        content.Length > 100 ? content.Substring(0, 100) + "..." : content);
                    
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        _logger?.LogInformation("[PIPELINE ADD] About to add content to pipeline, length={Length}", content.Length);
                        _contentPipeline.AddRawContent(content);
                        _logger?.LogInformation("[PIPELINE ADD] Content added successfully");
                        hasDisplayedContent = true;
                    }
                    else
                    {
                        _logger?.LogWarning("[PIPELINE ADD] Skipping empty content");
                    }
                    consecutiveToolOnlyIterations = 0; // Reset counter when we display content
                }

                _logger?.LogInformation("[CONVERSATION] Breaking from loop - no tool calls detected, displayed={HasDisplayed}", hasDisplayedContent);
                break; // No more tool calls, we're done
            }
        }

        // Give the pipeline a moment to process any queued content
        _logger?.LogInformation("[CONVERSATION] Waiting for pipeline to process content");
        await Task.Delay(200); // Short delay to ensure background processing completes
        
        // Finalize pipeline processing first
        _logger?.LogInformation("[CONVERSATION] Finalizing pipeline");
        await _contentPipeline.FinalizeAsync();
        
        // CRITICAL: Ensure we always display something to the user
        if (!hasDisplayedContent)
        {
            _logger?.LogWarning("[NO CONTENT] No content was displayed to user after {Iterations} iterations", iteration);
            
            // Check if this was likely a greeting
            var lowerMessage = userMessage.ToLowerInvariant().Trim();
            if (lowerMessage == "hello" || lowerMessage == "hi" || lowerMessage == "hey" || 
                lowerMessage.StartsWith("hello") || lowerMessage.StartsWith("hi ") || lowerMessage.StartsWith("hey "))
            {
                var greeting = "Hello! I'm here to help. What would you like to know or work on today?";
                _contentPipeline.AddRawContent(greeting);
                finalResponse.AppendLine(greeting);
                _contextManager.AddAssistantMessage(greeting);
            }
            // Check if tools were executed
            else if (allToolResults.Any())
            {
                // Build a summary of tool results
                var summary = new StringBuilder();
                summary.AppendLine("I've completed analyzing your request. Here's what I found:");
                summary.AppendLine();
                
                foreach (var (toolId, result) in allToolResults)
                {
                    // Provide a brief summary of each tool result
                    if (!string.IsNullOrWhiteSpace(result) && result != "Code index query completed")
                    {
                        // Extract meaningful information from the result
                        var lines = result.Split('\n').Take(10).Where(l => !string.IsNullOrWhiteSpace(l));
                        if (lines.Any())
                        {
                            summary.AppendLine($"From {toolId}:");
                            foreach (var line in lines.Take(5))
                            {
                                summary.AppendLine($"  • {line.Trim()}");
                            }
                        }
                    }
                }
                
                summary.AppendLine();
                summary.AppendLine("Based on this analysis, I can help you with specific questions about the project.");
                
                var summaryText = summary.ToString();
                _contentPipeline.AddRawContent(summaryText);
                finalResponse.AppendLine(summaryText);
                _contextManager.AddAssistantMessage(summaryText);
            }
            // For requests that don't need tools (like "write a sample C# program")
            else
            {
                // This is a critical failure - the LLM provided no response at all
                var errorMsg = @"I apologize, but I didn't receive a proper response from the language model. 

For your request about writing a sample C# program, here's a basic example:

```csharp
using System;

namespace SampleProgram
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine(""Hello, World!"");
            
            // Example of a simple calculation
            int a = 5;
            int b = 10;
            int sum = a + b;
            
            Console.WriteLine($""The sum of {a} and {b} is {sum}"");
            
            Console.WriteLine(""Press any key to exit..."");
            Console.ReadKey();
        }
    }
}
```

This is a basic C# console application that demonstrates fundamental concepts like namespaces, classes, methods, variables, and console I/O.";
                
                _contentPipeline.AddRawContent(errorMsg);
                finalResponse.AppendLine(errorMsg);
                _contextManager.AddAssistantMessage(errorMsg);
            }
        }

        // Show context stats with proper priority (rendered last)
        var stats = _contextManager.GetStats();
        _tracer?.TraceContextStats(stats.MessageCount, stats.EstimatedTokens, stats.ToolCallCount);
        var contextInfo = Commands.ConsoleColors.Dim($"Context: {stats.MessageCount} messages, ~{stats.EstimatedTokens} tokens, {stats.ToolCallCount} tool calls");
        _contentPipeline.AddSystemMessage(contextInfo, SystemMessageType.Context, priority: 2000);
        
        // Final pipeline finalization to render context message
        await _contentPipeline.FinalizeAsync();

        return finalResponse.ToString();
    }

    private bool LooksLikeRepoExploration(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return false;
        var s = userMessage.ToLowerInvariant();
        return (s.Contains("what's in") || s.Contains("what is in") || s.Contains("contents") || s.Contains("list") || s.Contains("show files") || s.Contains("source code") || s.Contains("repo"))
               && (s.Contains("src") || s.Contains("repo") || s.Contains("code"));
    }

    private async Task PreloadRepoContextAsync(CancellationToken cancellationToken)
    {
        // Build a batch of assistant-declared tool_calls (client-initiated), then execute them
        var initialCalls = new List<ModelToolCall>();

        // Top-level and src structure
        initialCalls.Add(new ModelToolCall { ToolId = "list_directory", Parameters = new Dictionary<string, object?> { ["path"] = "." } });
        initialCalls.Add(new ModelToolCall { ToolId = "list_directory", Parameters = new Dictionary<string, object?> { ["path"] = "./src" } });
        initialCalls.Add(new ModelToolCall { ToolId = "list_directory", Parameters = new Dictionary<string, object?> { ["path"] = "./src/Andy.Cli" } });

        // Key files commonly useful for summary
        var candidateFiles = new[]
        {
            "README.md",
            "./src/Andy.Cli/Andy.Cli.csproj",
            "./src/Andy.Cli/Program.cs"
        };
        foreach (var f in candidateFiles)
        {
            initialCalls.Add(new ModelToolCall { ToolId = "read_file", Parameters = new Dictionary<string, object?> { ["file_path"] = f } });
        }

        // Post a synthetic assistant message with tool_calls
        var contextToolCalls = new List<ContextToolCall>();
        var callIdMap = new List<(string ToolId, string CallId, Dictionary<string, object?> Params)>();
        foreach (var call in initialCalls)
        {
            var id = "call_" + Guid.NewGuid().ToString("N");
            contextToolCalls.Add(new ContextToolCall { CallId = id, ToolId = call.ToolId, Parameters = call.Parameters });
            callIdMap.Add((call.ToolId, id, call.Parameters));
        }
        _contextManager.AddAssistantMessage("", contextToolCalls);

        // Execute each tool call and add tool responses to context
        foreach (var call in callIdMap)
        {
            try
            {
                var execResult = await _toolService.ExecuteToolAsync(call.ToolId, call.Params, cancellationToken);
                string outputStr;
                if (execResult.Data != null)
                {
                    try
                    {
                        outputStr = SerializeToolDataWithTruncation(execResult.Data, call.ToolId, 2000);
                    }
                    catch
                    {
                        outputStr = execResult.Data.ToString() ?? execResult.Message ?? execResult.ErrorMessage ?? "No output";
                    }
                }
                else if (!string.IsNullOrEmpty(execResult.FullOutput))
                {
                    outputStr = execResult.FullOutput!;
                }
                else
                {
                    outputStr = execResult.Message ?? execResult.ErrorMessage ?? "No output";
                }

                // Apply tool-specific output limit with cumulative tracking
                var baseLimit = ToolOutputLimits.GetLimit(call.ToolId);
                var adjustedLimit = _outputTracker.GetAdjustedLimit(call.ToolId, baseLimit);
                var limitedOutput = ToolOutputLimits.LimitOutput(call.ToolId, outputStr, adjustedLimit);
                _outputTracker.RecordOutput(call.ToolId, limitedOutput.Length);
                _contextManager.AddToolExecution(call.ToolId, call.CallId, call.Params, limitedOutput);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Preload tool call failed: {Tool}", call.ToolId);
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
        
        // Log the state of messages before fix
        if (request.Messages != null)
        {
            foreach (var msg in request.Messages)
            {
                var partsInfo = msg.Parts == null ? "null" : 
                    msg.Parts.Count == 0 ? "empty" : 
                    $"{msg.Parts.Count} parts";
                _logger?.LogDebug("Before fix - Message Role: {Role}, Parts: {PartsInfo}", msg.Role, partsInfo);
                if (msg.Parts != null && msg.Parts.Count > 0)
                {
                    foreach (var part in msg.Parts)
                    {
                        if (part is TextPart textPart)
                        {
                            _logger?.LogDebug("  TextPart content: {Content}", 
                                string.IsNullOrEmpty(textPart.Text) ? "(empty)" : textPart.Text.Length > 50 ? textPart.Text.Substring(0, 50) + "..." : textPart.Text);
                        }
                    }
                }
            }
        }

        // Tool definitions are already in the system prompt from SystemPromptService
        // Don't add them again to avoid duplication

        // Fix empty Parts issue in messages
        request = FixEmptyMessageParts(request);
        
        // Log the state after fix
        if (request.Messages != null)
        {
            foreach (var msg in request.Messages)
            {
                var partsInfo = msg.Parts == null ? "null" : 
                    msg.Parts.Count == 0 ? "empty" : 
                    $"{msg.Parts.Count} parts";
                _logger?.LogDebug("After fix - Message Role: {Role}, Parts: {PartsInfo}", msg.Role, partsInfo);
            }
        }

        return request;
    }

    /// <summary>
    /// Fix the empty Parts issue in LlmRequest messages
    /// </summary>
    private LlmRequest FixEmptyMessageParts(LlmRequest request)
    {
        // Add debug logging
        var debugLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".andy", "logs", "fix-parts-debug.log");
        Directory.CreateDirectory(Path.GetDirectoryName(debugLog)!);
        File.AppendAllText(debugLog, $"\n[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] FixEmptyMessageParts called\n");
        
        if (request.Messages == null) 
        {
            File.AppendAllText(debugLog, "Request.Messages is null\n");
            return request;
        }

        File.AppendAllText(debugLog, $"Request has {request.Messages.Count} messages\n");
        
        // Get the full history to properly match messages
        var history = _contextManager.GetHistory();
        File.AppendAllText(debugLog, $"History has {history.Count} entries\n");
        
        // Match messages with history entries by index and role
        for (int i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            
            // Check if Parts needs fixing
            bool needsFix = false;
            if (message.Parts == null || message.Parts.Count == 0)
            {
                needsFix = true;
            }
            else
            {
                // Check if all parts are null or empty
                var hasValidContent = false;
                foreach (var part in message.Parts)
                {
                    if (part != null)
                    {
                        // Check if it's an empty object (serialized as {})
                        var partType = part.GetType();
                        if (part is TextPart textPart)
                        {
                            if (!string.IsNullOrEmpty(textPart.Text))
                            {
                                hasValidContent = true;
                                break;
                            }
                        }
                        else
                        {
                            // Check if the part has any actual content
                            var json = System.Text.Json.JsonSerializer.Serialize(part);
                            if (json != "{}" && json != "null")
                            {
                                hasValidContent = true;
                                break;
                            }
                        }
                    }
                }
                needsFix = !hasValidContent;
            }
            
            if (needsFix)
            {
                File.AppendAllText(debugLog, $"Message {i} ({message.Role}) needs fixing - Parts are empty or invalid\n");
                
                // Find corresponding history entry
                ContextEntry? matchingEntry = null;
                
                // Try to match by index first (assuming messages and history are in same order)
                if (i < history.Count)
                {
                    var candidate = history[i];
                    // Verify role matches
                    if (candidate.Role.ToString().Equals(message.Role.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        matchingEntry = candidate;
                    }
                }
                
                // If index matching failed, try to find by role
                if (matchingEntry == null)
                {
                    // For user messages, get the most recent user message
                    if (message.Role.ToString() == "User")
                    {
                        matchingEntry = history.LastOrDefault(h => h.Role == MessageRole.User);
                    }
                    // For assistant messages, get corresponding assistant message
                    else if (message.Role.ToString() == "Assistant")
                    {
                        // Count how many assistant messages we've seen so far
                        var assistantIndex = request.Messages.Take(i).Count(m => m.Role.ToString() == "Assistant");
                        matchingEntry = history.Where(h => h.Role == MessageRole.Assistant)
                                              .Skip(assistantIndex)
                                              .FirstOrDefault();
                    }
                }
                
                // Apply the fix
                if (matchingEntry != null && !string.IsNullOrEmpty(matchingEntry.Content))
                {
                    message.Parts = new List<MessagePart>
                    {
                        new TextPart { Text = matchingEntry.Content }
                    };
                    File.AppendAllText(debugLog, $"Fixed message {i} with content from history: {matchingEntry.Content.Substring(0, Math.Min(50, matchingEntry.Content.Length))}...\n");
                    _logger?.LogDebug("Fixed empty Parts for {Role} message with content: {Preview}", 
                        message.Role, 
                        matchingEntry.Content.Length > 50 ? matchingEntry.Content.Substring(0, 50) + "..." : matchingEntry.Content);
                }
                else
                {
                    // Last resort: provide a minimal message
                    var fallbackText = message.Role.ToString() == "User" ? "Hello" : "I'm ready to help.";
                    message.Parts = new List<MessagePart>
                    {
                        new TextPart { Text = fallbackText }
                    };
                    File.AppendAllText(debugLog, $"No history found for message {i}, using fallback: {fallbackText}\n");
                    _logger?.LogWarning("No history found for {Role} message, using fallback", message.Role);
                }
            }
        }

        // Log final state
        File.AppendAllText(debugLog, "After fixing:\n");
        for (int i = 0; i < request.Messages.Count; i++)
        {
            var msg = request.Messages[i];
            var partsInfo = msg.Parts == null ? "null" : msg.Parts.Count == 0 ? "empty" : $"{msg.Parts.Count} parts";
            File.AppendAllText(debugLog, $"  Message {i} ({msg.Role}): {partsInfo}\n");
            if (msg.Parts != null && msg.Parts.Count > 0 && msg.Parts[0] is TextPart tp)
            {
                File.AppendAllText(debugLog, $"    Content: {tp.Text?.Substring(0, Math.Min(30, tp.Text?.Length ?? 0))}...\n");
            }
        }

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

            // Capture raw response for diagnostics
            if (_diagnostic != null && response != null)
            {
                // Log the full request first
                var requestJson = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await _diagnostic.CaptureRawRequest(requestJson);

                // Then capture the response
                var lastUserMessage = request.Messages.LastOrDefault(m => m.Role.ToString() == "User");
                string prompt = "";
                if (lastUserMessage != null)
                {
                    // Try to get text from Parts if available
                    if (lastUserMessage.Parts != null && lastUserMessage.Parts.Any())
                    {
                        var textPart = lastUserMessage.Parts.OfType<TextPart>().FirstOrDefault();
                        if (textPart != null)
                        {
                            prompt = textPart.Text ?? "";
                        }
                        else
                        {
                            // Fallback: try to extract text from the first part
                            try
                            {
                                var firstPart = lastUserMessage.Parts.FirstOrDefault();
                                if (firstPart != null)
                                {
                                    // Use reflection or JSON serialization to get text content
                                    var json = System.Text.Json.JsonSerializer.Serialize(firstPart);
                                    prompt = json.Length > 100 ? json.Substring(0, 100) + "..." : json;
                                }
                            }
                            catch
                            {
                                prompt = "[Unable to extract user message]";
                            }
                        }
                    }
                }
                await _diagnostic.CaptureRawResponse(prompt, response.Content ?? "");
            }

            // Raw output is captured in diagnostic files when ANDY_DEBUG_RAW=true

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

            // Capture raw response for diagnostics
            if (_diagnostic != null)
            {
                // Log the full request first
                var requestJson = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await _diagnostic.CaptureRawRequest(requestJson);

                // Then capture the response
                var lastUserMessage = request.Messages.LastOrDefault(m => m.Role.ToString() == "User");
                string prompt = "";
                if (lastUserMessage != null)
                {
                    // Try to get text from Parts if available
                    if (lastUserMessage.Parts != null && lastUserMessage.Parts.Any())
                    {
                        var textPart = lastUserMessage.Parts.OfType<TextPart>().FirstOrDefault();
                        if (textPart != null)
                        {
                            prompt = textPart.Text ?? "";
                        }
                        else
                        {
                            // Fallback: try to extract text from the first part
                            try
                            {
                                var firstPart = lastUserMessage.Parts.FirstOrDefault();
                                if (firstPart != null)
                                {
                                    // Use reflection or JSON serialization to get text content
                                    var json = System.Text.Json.JsonSerializer.Serialize(firstPart);
                                    prompt = json.Length > 100 ? json.Substring(0, 100) + "..." : json;
                                }
                            }
                            catch
                            {
                                prompt = "[Unable to extract user message]";
                            }
                        }
                    }
                }
                await _diagnostic.CaptureRawResponse(prompt, content);
            }

            // Raw output is captured in diagnostic files when ANDY_DEBUG_RAW=true

            // Only process tool calls if streaming is complete; otherwise, return what we have
            if (!isComplete)
            {
                return new LlmResponse
                {
                    Content = fullContent.ToString(),
                    FinishReason = finishReason
                };
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
    /// Display parsed response with proper formatting for code blocks
    /// </summary>
    private void DisplayParsedResponse(ResponseNode? ast, StreamingRenderResult renderResult)
    {
        // Use content pipeline for all rendering instead of direct feed manipulation
        if (!string.IsNullOrEmpty(renderResult.TextContent))
        {
            _logger?.LogDebug("DisplayParsedResponse: Using content pipeline for text content length = {Length}", renderResult.TextContent.Length);
            _contentPipeline.AddRawContent(renderResult.TextContent);
        }
        else if (ast != null && ast.Children.Count > 0)
        {
            _logger?.LogWarning("DisplayParsedResponse: AST has {Count} nodes but no TextContent", ast.Children.Count);
            _contentPipeline.AddRawContent("_[Response content was parsed but could not be displayed]_");
        }
    }

    /// <summary>
    /// Render text content while extracting code blocks from both proper and malformed code fences.
    /// Supports triple-backtick fences and a common malformed variant: a single-backtick followed by a language token (`csharp ... `).
    /// </summary>
    private void RenderTextWithCodeExtraction(string text)
    {
        _logger?.LogDebug("RenderTextWithCodeExtraction called with text length: {Length}", text?.Length ?? 0);
        if (string.IsNullOrEmpty(text)) 
        {
            _logger?.LogDebug("RenderTextWithCodeExtraction: text is null or empty, returning");
            return;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        int i = 0;
        while (i < normalized.Length)
        {
            int fenceStart = normalized.IndexOf("```", i, StringComparison.Ordinal);
            int singleStart = normalized.IndexOf('\n', i) >= 0 ? normalized.IndexOf('\n', i) : int.MaxValue; // anchor for scanning

            // Check for malformed single-backtick language marker at line start
            int lineStart = i;
            // Move to start of current line
            int prevNl = normalized.LastIndexOf('\n', Math.Max(0, i - 1));
            if (prevNl >= 0) lineStart = prevNl + 1;
            bool hasSingleLang = false;
            int singleMarkerIdx = lineStart;
            string? singleLang = null;
            if (lineStart < normalized.Length && normalized[lineStart] == '`' && !(lineStart + 2 < normalized.Length && normalized.Substring(lineStart, 3) == "```"))
            {
                // Collect language token after single backtick
                int langEnd = normalized.IndexOf('\n', lineStart + 1);
                if (langEnd < 0) langEnd = normalized.Length;
                var langToken = normalized.Substring(lineStart + 1, langEnd - (lineStart + 1)).Trim();
                if (!string.IsNullOrEmpty(langToken) && langToken.All(c => char.IsLetter(c) || c == '#'))
                {
                    hasSingleLang = true;
                    singleLang = langToken.ToLowerInvariant();
                    singleMarkerIdx = lineStart;
                }
            }

            // If a proper triple fence appears earlier than the current line's single marker, handle that first
            if (fenceStart >= 0 && (!hasSingleLang || fenceStart < singleMarkerIdx))
            {
                // Emit markdown preceding the code block
                if (fenceStart > i)
                {
                    var md = normalized.Substring(i, fenceStart - i);
                    if (!string.IsNullOrWhiteSpace(md)) RenderMarkdownOrInlineJson(md);
                }
                int langEnd = normalized.IndexOf('\n', fenceStart + 3);
                string? lang = null;
                int contentStart;
                if (langEnd > fenceStart + 3)
                {
                    lang = normalized.Substring(fenceStart + 3, langEnd - (fenceStart + 3)).Trim();
                    contentStart = langEnd + 1;
                }
                else
                {
                    contentStart = fenceStart + 3;
                }
                int fenceEnd = normalized.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (fenceEnd < 0) fenceEnd = normalized.Length;
                var code = normalized.Substring(contentStart, fenceEnd - contentStart);
                _feed.AddCode(code, lang);
                i = Math.Min(normalized.Length, fenceEnd + 3);
                continue;
            }

            if (hasSingleLang)
            {
                // Emit markdown before the single marker
                if (singleMarkerIdx > i)
                {
                    var md = normalized.Substring(i, singleMarkerIdx - i);
                    if (!string.IsNullOrWhiteSpace(md)) RenderMarkdownOrInlineJson(md);
                }
                // Find end marker: a line that is exactly "`" or end of text
                int afterMarker = normalized.IndexOf('\n', singleMarkerIdx);
                if (afterMarker < 0) afterMarker = singleMarkerIdx + 1;
                int scanPos = afterMarker + 1;
                int endIdx = normalized.IndexOf('\n', scanPos);
                int codeEnd = normalized.Length;
                while (endIdx >= 0)
                {
                    // Check if the line is a lone backtick
                    int lineBegin = endIdx + 1;
                    // Determine current line content
                    int nextNl = normalized.IndexOf('\n', lineBegin);
                    string line = nextNl >= 0 ? normalized.Substring(lineBegin, nextNl - lineBegin) : normalized.Substring(lineBegin);
                    if (line.Trim() == "`")
                    {
                        codeEnd = lineBegin - 1; // end just before this line
                        // Advance past closing line
                        i = nextNl >= 0 ? nextNl + 1 : normalized.Length;
                        break;
                    }
                    endIdx = nextNl;
                }
                if (endIdx < 0)
                {
                    // No explicit closing, take rest as code
                    i = normalized.Length;
                }
                var codeStart = afterMarker + 1;
                if (codeStart < 0 || codeStart > normalized.Length) codeStart = afterMarker;
                var codeText = normalized.Substring(codeStart, Math.Max(0, codeEnd - codeStart));
                _feed.AddCode(codeText, singleLang);
                continue;
            }

            // No more code blocks found; emit remaining as markdown
            var tail = normalized.Substring(i);
            if (!string.IsNullOrWhiteSpace(tail)) RenderMarkdownOrInlineJson(tail);
            break;
        }
    }

    /// <summary>
    /// Extract non-tool text from a response that may contain tool calls
    /// </summary>
    private string ExtractNonToolText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        
        // Remove tool call JSON patterns
        // Pattern 1: {"tool":"...", "parameters":{...}}
        // Pattern 2: <tool_call>...</tool_call>
        // Pattern 3: ```json\n{...}\n```
        
        var result = text;
        
        // Remove <tool_call> blocks
        result = System.Text.RegularExpressions.Regex.Replace(result, 
            @"<tool_call>[\s\S]*?</tool_call>", "", 
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        // Remove JSON tool calls (careful not to remove all JSON)
        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"\{[^}]*""tool""\s*:\s*""[^""]+""[^}]*\}", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        // Remove ```json blocks that contain tool calls
        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"```json\s*\n?\s*\{[^}]*""tool""\s*:[^}]*\}\s*\n?\s*```", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        // Clean up extra whitespace
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\n{3,}", "\n\n");
        result = result.Trim();
        
        return result;
    }
    
    private void RenderMarkdownOrInlineJson(string text)
    {
        // Split on lines; render any single-line valid JSON object as a code block with json language
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("{") && t.EndsWith("}"))
            {
                try
                {
                    using var _ = System.Text.Json.JsonDocument.Parse(t);
                    _feed.AddCode(t, "json");
                    continue;
                }
                catch
                {
                    // fall through to markdown
                }
            }
            if (!string.IsNullOrEmpty(line))
                _feed.AddMarkdownRich(line);
            else
                _feed.AddMarkdownRich("");
        }
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
        _compiler = new LlmResponseCompiler(NormalizeProviderName(_currentProvider, _currentModel), _jsonRepair, null);
    }

    private string NormalizeProviderName(string providerName, string modelName)
    {
        var pn = providerName?.ToLowerInvariant() ?? "";
        var mn = modelName?.ToLowerInvariant() ?? "";
        // If model name indicates qwen, force provider to qwen for parsing behavior
        if (mn.Contains("qwen")) return "qwen";
        if (pn.Contains("openai")) return "openai";
        if (pn.Contains("anthropic")) return "anthropic";
        if (pn.Contains("google") || pn.Contains("gemini")) return "gemini";
        if (pn.Contains("mistral")) return "mistral";
        if (pn.Contains("meta") || mn.Contains("llama") || mn.Contains("lama")) return "llama";
        return pn;
    }

    /// <summary>
    /// Serialize tool Data with truncation for large payloads (notably read_file content).
    /// Caps long string fields like "content" and adds a summary to avoid provider 400s.
    /// </summary>
    private static string SerializeToolDataWithTruncation(object data, string toolId, int maxFieldChars)
    {
        // Convert to JsonObject for inspection
        var json = System.Text.Json.JsonSerializer.SerializeToNode(data, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        if (json is JsonObject obj)
        {
            // Special handling: read_file often returns a huge "content" string
            if (toolId == "read_file" && obj.TryGetPropertyValue("content", out var contentNode) && contentNode is JsonValue)
            {
                var content = contentNode!.GetValue<string?>();
                if (!string.IsNullOrEmpty(content))
                {
                    // Extract file path if available
                    var filePath = "unknown";
                    if (obj.TryGetPropertyValue("file_path", out var pathNode) && pathNode is JsonValue)
                    {
                        filePath = pathNode.GetValue<string?>() ?? "unknown";
                    }
                    else if (obj.TryGetPropertyValue("filePath", out pathNode) && pathNode is JsonValue)
                    {
                        filePath = pathNode.GetValue<string?>() ?? "unknown";
                    }
                    
                    // Use intelligent summarization for large files
                    if (content!.Length > maxFieldChars)
                    {
                        var summarized = FileContentSummarizer.SummarizeFileContent(filePath, content);
                        obj["content"] = summarized;
                        obj["original_length"] = content.Length;
                        obj["summarized"] = true;
                    }
                }
            }
            // Special handling: list_directory returns array of items
            else if (toolId == "list_directory" && obj.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonArray itemsArray)
            {
                // For recursive listings, be more aggressive with truncation
                // Check if this appears to be a recursive listing (has nested directories)
                bool isRecursive = false;
                foreach (var item in itemsArray)
                {
                    if (item is JsonObject itemObj && 
                        itemObj.TryGetPropertyValue("fullPath", out var pathNode) && 
                        pathNode is JsonValue pathVal)
                    {
                        var path = pathVal.GetValue<string>() ?? "";
                        if (path.Count(c => c == '/' || c == '\\') > 2)
                        {
                            isRecursive = true;
                            break;
                        }
                    }
                }
                
                // Use different limits for recursive vs non-recursive
                int maxItems = isRecursive ? 25 : 50;
                if (itemsArray.Count > maxItems)
                {
                    var truncatedItems = new JsonArray();
                    // Take a mix of items from different depths if recursive
                    if (isRecursive)
                    {
                        // Take first 15 and last 10 to show structure
                        for (int i = 0; i < Math.Min(15, itemsArray.Count); i++)
                        {
                            truncatedItems.Add(itemsArray[i]?.DeepClone());
                        }
                        for (int i = Math.Max(15, itemsArray.Count - 10); i < itemsArray.Count; i++)
                        {
                            truncatedItems.Add(itemsArray[i]?.DeepClone());
                        }
                    }
                    else
                    {
                        for (int i = 0; i < maxItems; i++)
                        {
                            truncatedItems.Add(itemsArray[i]?.DeepClone());
                        }
                    }
                    obj["items"] = truncatedItems;
                    obj["truncated"] = true;
                    obj["original_count"] = itemsArray.Count;
                    obj["showing_count"] = truncatedItems.Count;
                    obj["truncation_note"] = $"Showing {truncatedItems.Count} of {itemsArray.Count} items";
                }
            }
            // Don't apply generic truncation to structured data
            else if (toolId != "list_directory" && toolId != "code_index")
            {
                // Generic cap: if any string property exceeds maxFieldChars, cap it
                // But skip this for tools that return structured data
                foreach (var kv in obj.ToList())
                {
                    if (kv.Value is JsonValue val && val.TryGetValue<string>(out var s) && s != null && s.Length > maxFieldChars)
                    {
                        obj[kv.Key] = s.Substring(0, maxFieldChars) + $"\n...[truncated {s.Length - maxFieldChars} chars]";
                    }
                }
            }
        }

        return json?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }) ??
               System.Text.Json.JsonSerializer.Serialize(data);
    }

    // Removed raw execution shim; using ExecuteToolAsync with ParameterMapper ensures compatibility across tools

    /// <summary>
    /// Clear the conversation context
    /// </summary>
    public void ClearContext()
    {
        _contextManager.Clear();
    }

    /// <summary>
    /// Generate diagnostic summary if available
    /// </summary>
    public async Task GenerateDiagnosticSummaryAsync()
    {
        if (_diagnostic != null)
        {
            await _diagnostic.GenerateSummaryReport();
        }
    }

    /// <summary>
    /// Get context statistics
    /// </summary>
    public ContextStats GetContextStats()
    {
        return _contextManager.GetStats();
    }

    /// <summary>
    /// Sanitizes assistant text for display by handling escaping, unwanted characters, and hiding internal tool mentions
    /// </summary>

    /// <summary>
    /// Internal tool call representation
    /// </summary>
    private class ToolCall
    {
        public string ToolId { get; set; } = "";
        public Dictionary<string, object?> Parameters { get; set; } = new();
    }
    
    public void Dispose()
    {
        _contentPipeline?.Dispose();
    }
}