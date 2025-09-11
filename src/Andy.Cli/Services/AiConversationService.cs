using System.Text;
using Andy.Cli.Diagnostics;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
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
    private readonly ILogger<AiConversationService>? _logger;
    private readonly QwenResponseDiagnostic? _diagnostic;
    private readonly IToolCallValidator _toolCallValidator;
    private readonly IJsonRepairService _jsonRepair;
    private readonly ConversationTracer? _tracer;
    private readonly CumulativeOutputTracker _outputTracker = new();
    private readonly List<FunctionCall> _pendingFunctionCalls = new();
    private ContentPipeline.ContentPipeline _contentPipeline;
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
        // no-op
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
        // Prefer streaming when tools might be used so we can consume structured FunctionCall items
        var useStreaming = enableStreaming;
        // Recreate content pipeline for this request to avoid finalized state carrying over
        _contentPipeline?.Dispose();
        var processor = new ContentPipeline.MarkdownContentProcessor();
        var sanitizer = new ContentPipeline.TextContentSanitizer();
        var renderer = new ContentPipeline.FeedContentRenderer(_feed, _logger as ILogger<ContentPipeline.FeedContentRenderer>);
        _contentPipeline = new ContentPipeline.ContentPipeline(processor, sanitizer, renderer, _logger as ILogger<ContentPipeline.ContentPipeline>);
        // Trace user message
        _tracer?.TraceUserMessage(userMessage);
        
        // Reset cumulative output tracker for new message
        _outputTracker.Reset();
        
        // Add user message to context
        _contextManager.AddUserMessage(userMessage);

        // Get available tools
        var availableTools = _toolRegistry.GetTools(enabledOnly: true).ToList();
 
        // Local shortcut: answer simple environment questions without LLM
        if (LooksLikeCurrentDirectoryQuery(userMessage))
        {
            var cwd = Directory.GetCurrentDirectory();
            var msg = $"Current directory: {cwd}";
            _contentPipeline.AddRawContent(msg);
            _contextManager.AddAssistantMessage(msg);
            await _contentPipeline.FinalizeAsync();
            return msg;
        }
        
        // Create the LLM request with tool definitions (if appropriate)
        var request = CreateRequestWithTools(availableTools);
        
        // DISABLED: Preloading causes issues with proper response generation
        // The LLM gets confused when it sees pre-executed tool results
        // and doesn't provide a proper summary of findings
        /*
        // Heuristic: proactively explore repository structure for repo/content questions
        if (LooksLikeRepoExploration(userMessage))
        {
            try
            {
                await PreloadRepoContextAsync(cancellationToken);
                // Refresh request after preloading tool results
                request = _contextManager.GetContext().CreateRequest(_currentModel);
                // Fix empty Parts issue in messages
                request = FixEmptyMessageParts(request);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "PreloadRepoContextAsync failed");
            }
        }
        */

        // Track the conversation loop
        var maxIterations = 12; // Allow deeper multi-step planning and execution
        var iteration = 0;
        var finalResponse = new StringBuilder();
        var allToolResults = new List<(string ToolId, string Result)>(); // Track all tool executions
        var hasDisplayedContent = false; // Track if we've displayed any content to the user
        var consecutiveToolOnlyIterations = 0; // Track consecutive iterations with only tool calls
        const int maxConsecutiveToolIterations = 3; // Break after 3 consecutive tool-only iterations
        var toolFallbackTried = false; // Retry once without tools on provider 400s

        while (iteration < maxIterations)
        {
            iteration++;
            _logger?.LogInformation("[ITERATION START] Beginning iteration {Iteration} of {Max}", iteration, maxIterations);
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
                    SystemPrompt = request.SystemPrompt?.Substring(0, Math.Min(2000, request.SystemPrompt?.Length ?? 0)) + (request.SystemPrompt?.Length > 2000 ? "..." : "")
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
            
            var response = useStreaming
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
            }

            if (response == null)
            {
                // Fallback: if tools were included and request failed, retry once without tools
                if ((request.Tools?.Any() ?? false) && !toolFallbackTried)
                {
                    _logger?.LogWarning("[FALLBACK] LLM response was null; retrying once without tools");
                    toolFallbackTried = true;
                    request = CreateRequestWithTools(new List<ToolRegistration>());
                    // Ensure Parts are fixed
                    request = FixEmptyMessageParts(request);
                    // Retry this iteration without incrementing iteration further
                    iteration--; // Counteract the ++ at loop start to keep iteration numbering stable
                    continue;
                }
                break;
            }

            // If streaming provided structured function calls, handle them directly without custom parsing
            if (useStreaming && _pendingFunctionCalls.Any())
            {
                _logger?.LogInformation("[STRUCTURED CALLS] Handling {Count} function calls from streaming", _pendingFunctionCalls.Count);

                // Generate call IDs for each and add as assistant tool_calls
                var contextToolCalls = new List<ContextToolCall>();
                var callIdMap = new Dictionary<int, string>();
                for (int i = 0; i < _pendingFunctionCalls.Count; i++)
                {
                    var fc = _pendingFunctionCalls[i];
                    var generatedCallId = string.IsNullOrWhiteSpace(fc.Id) ? ("call_" + Guid.NewGuid().ToString("N")) : fc.Id;
                    callIdMap[i] = generatedCallId;
                    contextToolCalls.Add(new ContextToolCall
                    {
                        CallId = generatedCallId,
                        ToolId = fc.Name,
                        Parameters = fc.Arguments ?? new Dictionary<string, object?>()
                    });
                }

                var assistantText = !string.IsNullOrWhiteSpace(response?.Content) ? response!.Content! : "(Executing tools...)";
                _contextManager.AddAssistantMessage(assistantText, contextToolCalls);

                // Execute tools
                var toolResults = new List<string>();
                for (int i = 0; i < _pendingFunctionCalls.Count; i++)
                {
                    var fc = _pendingFunctionCalls[i];
                    var thisCallId = callIdMap[i];

                    var toolId = fc.Name;
                    var parameters = fc.Arguments ?? new Dictionary<string, object?>();

                    var execResult = await _toolService.ExecuteToolAsync(toolId, parameters, cancellationToken);

                    string outputStr;
                    if (execResult.Data != null)
                    {
                        try
                        {
                            int maxChars = toolId == "list_directory" ? 5000 : 2000;
                            outputStr = SerializeToolDataWithTruncation(execResult.Data, toolId, maxChars);
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

                    var baseLimit = ToolOutputLimits.GetLimit(toolId);
                    var adjustedLimit = _outputTracker.GetAdjustedLimit(toolId, baseLimit);
                    var limitedOutput = ToolOutputLimits.LimitOutput(toolId, outputStr, adjustedLimit);
                    _outputTracker.RecordOutput(toolId, limitedOutput.Length);

                    _contextManager.AddToolExecution(toolId, thisCallId, parameters, limitedOutput);
                    toolResults.Add(outputStr);
                    allToolResults.Add((toolId, limitedOutput));
                }

                // Prepare for next iteration
                _pendingFunctionCalls.Clear();
                request = _contextManager.GetContext().CreateRequest(_currentModel);
                request = FixEmptyMessageParts(request);
                _logger?.LogInformation("[STRUCTURED CALLS] Continuing to next iteration after tool execution");
                continue;
            }

            // Structured path: no custom parsing; treat response.Content as text
            var rawContent = response.Content ?? "";
            string? additionalText = null;
            
            _logger?.LogInformation("[STRUCTURED RESULT] Iteration {Iteration}: Text length={TextLength}, Preview='{Preview}'",
                iteration,
                rawContent?.Length ?? 0,
                rawContent?.Length > 100 ? rawContent.Substring(0, 100) + "..." : rawContent ?? "(null)");
 
            if (_pendingFunctionCalls.Any())
            {
                _logger?.LogInformation("[TOOL CALLS FOUND] Found {Count} tool calls in iteration {Iteration}", 
                    _pendingFunctionCalls.Count, iteration);
                 
                // If there's text content along with tool calls, display it
                var textToDisplay = rawContent;
                if (!string.IsNullOrWhiteSpace(textToDisplay))
                {
                    var textWithoutTools = ExtractNonToolText(textToDisplay);
                    if (!string.IsNullOrWhiteSpace(textWithoutTools))
                    {
                        _logger?.LogInformation("[TOOL WITH TEXT] Displaying text with tool execution: '{Preview}'",
                            textWithoutTools.Length > 100 ? textWithoutTools.Substring(0, 100) + "..." : textWithoutTools);
                        _contentPipeline.AddRawContent(textWithoutTools);
                        finalResponse.AppendLine(textWithoutTools);
                        hasDisplayedContent = true;
                    }
                }
 
                // Generate call IDs for each tool call and add them as assistant tool_calls
                var contextToolCalls = new List<ContextToolCall>();
                var callIdMap = new Dictionary<int, string>();
                for (int i = 0; i < _pendingFunctionCalls.Count; i++)
                {
                    var generatedCallId = "call_" + Guid.NewGuid().ToString("N");
                    callIdMap[i] = generatedCallId;
                    contextToolCalls.Add(new ContextToolCall
                    {
                        CallId = generatedCallId,
                        ToolId = _pendingFunctionCalls[i].Name,
                        Parameters = _pendingFunctionCalls[i].Arguments ?? new Dictionary<string, object?>()
                    });
                }
 
                // Add assistant message with tool calls to context
                // This is critical - the assistant message must include tool_calls
                // If there's no text content, use a placeholder that indicates tool execution
                var assistantText = !string.IsNullOrWhiteSpace(rawContent) 
                    ? rawContent 
                    : "(Executing tools...)";
                _contextManager.AddAssistantMessage(assistantText, contextToolCalls);
 
                // Execute tools and collect results
                var toolResults = new List<string>();
 
                for (int i = 0; i < _pendingFunctionCalls.Count; i++)
                {
                    var fc = _pendingFunctionCalls[i];
                    var thisCallId = callIdMap[i];
 
                    // Validate and optionally repair parameters before execution
                    var toolMetadata = _toolRegistry.GetTool(fc.Name)?.Metadata;
                    var callToExecute = new ModelToolCall { ToolId = fc.Name, Parameters = fc.Arguments ?? new Dictionary<string, object?>() };
                    if (toolMetadata != null)
                    {
                        var validation = _toolCallValidator.Validate(callToExecute, toolMetadata);
                        if (!validation.IsValid && validation.RepairedCall != null)
                        {
                            _logger?.LogWarning("Parameters for tool '{Tool}' were invalid; applying single repair attempt.", fc.Name);
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
                request = _contextManager.GetContext().CreateRequest(_currentModel);
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
                        request = _contextManager.GetContext().CreateRequest(_currentModel);
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
                _logger?.LogInformation("[TOOL PATH] Will request another LLM response with tool results in context");
                continue; // Continue to next iteration, don't process as regular text
            }
            else
            {
                // Regular text response - rendered content without tool calls
                var content = rawContent;
                
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
                    
                    // Fallback parser removed; no tool extraction here
                    var fallbackCalls = new List<ModelToolCall>();
                    
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

                        var assistantTextFallback = !string.IsNullOrWhiteSpace(rawContent) 
                            ? rawContent 
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

                            // Execute with standard path so tool invocation is displayed in the feed
                            var execResult = await _toolService.ExecuteToolAsync(
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

                        // We executed fallback tool calls; continue to next iteration to get model's summary
                        request = _contextManager.GetContext().CreateRequest(_currentModel);
                        request = FixEmptyMessageParts(request);
                        continue;
                    }

                    // Check if content is empty or seems incomplete
                    if (string.IsNullOrWhiteSpace(content) || 
                        (content.EndsWith(":") && content.Length < 50) || // Incomplete response like "No special action is required in this case:"
                        (response.Content?.Length > content.Length * 2)) // Raw response is significantly longer
                    {
                        _logger?.LogWarning("[EMPTY/INCOMPLETE CONTENT] Rendered: '{Rendered}' (length {RenderLen}), raw response length: {RawLength}", 
                            content, content.Length, response.Content?.Length ?? 0);
                        // Try to use the raw response content if render result is empty or incomplete
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

    private bool LooksLikeCurrentDirectoryQuery(string userMessage)
    {
        var s = userMessage?.ToLowerInvariant().Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(s)) return false;
        // Only treat as local CWD query if it's a short, direct question
        if (s.Length > 80) return false;
        bool mentionsCwd = s.Contains("current directory") || s.Contains("cwd") || s.Contains("pwd");
        if (!mentionsCwd) return false;
        // If also asking about repo/files/contents, defer to tools/LLM
        if (s.Contains("repo") || s.Contains("repository") || s.Contains("files") || s.Contains("file") || s.Contains("contents") || s.Contains("what can you tell"))
            return false;
        return true;
    }
    
    /// <summary>
    /// Create LLM request with tool definitions
    /// </summary>
    private LlmRequest CreateRequestWithTools(List<ToolRegistration> tools)
    {
        var context = _contextManager.GetContext();
        var request = context.CreateRequest(_currentModel);
        
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

        // Add tool definitions to the request
        // The LLM needs these to know what tools are available
        if (tools != null && tools.Any())
        {
            // Declare all registered tools to the model
            request.Tools = tools.Select(t => new ToolDeclaration
            {
                Name = t.Metadata.Id,
                Description = t.Metadata.Description,
                Parameters = ConvertParametersToSchema(t.Metadata.Parameters)
            }).ToList();
        }

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

            // Never mutate Tool messages into TextParts; ensure they carry ToolResponsePart
            if (message.Role == Andy.Llm.Models.MessageRole.Tool)
            {
                var hasToolResponse = message.Parts != null && message.Parts.Any(p => p is ToolResponsePart);
                if (!hasToolResponse)
                {
                    File.AppendAllText(debugLog, $"Message {i} (Tool) missing ToolResponsePart - attempting reconstruction from history\n");
                    // Find the most recent matching tool history entry
                    var toolEntry = history.LastOrDefault(h => h.Role == Andy.Cli.Services.MessageRole.Tool);
                    if (toolEntry != null)
                    {
                        var responseText = toolEntry.ToolResult ?? toolEntry.Content;
                        message.Parts = new List<MessagePart>
                        {
                            new ToolResponsePart
                            {
                                ToolName = toolEntry.ToolId ?? "tool",
                                CallId = toolEntry.ToolCallId ?? ("call_" + Guid.NewGuid().ToString("N")),
                                Response = responseText
                            }
                        };
                        File.AppendAllText(debugLog, $"Reconstructed ToolResponsePart for message {i} from history (tool={toolEntry.ToolId}, callId={toolEntry.ToolCallId})\n");
                    }
                }
                // Skip further fixing for Tool messages
                continue;
            }
            
            // For non-tool messages, check if Parts needs fixing
            bool needsFix = false;
            if (message.Parts == null || message.Parts.Count == 0)
            {
                needsFix = true;
            }
            else
            {
                // Consider ToolCallPart/ToolResponsePart inherently valid; for TextPart ensure non-empty
                bool hasValidContent = false;
                foreach (var part in message.Parts)
                {
                    switch (part)
                    {
                        case TextPart textPart:
                            if (!string.IsNullOrEmpty(textPart.Text))
                                hasValidContent = true;
                            break;
                        case ToolCallPart:
                            hasValidContent = true;
                            break;
                        case ToolResponsePart trp:
                            {
                                bool responseHasContent = false;
                                if (trp.Response is string sResp)
                                {
                                    responseHasContent = !string.IsNullOrWhiteSpace(sResp);
                                }
                                else if (trp.Response != null)
                                {
                                    responseHasContent = true;
                                }
                                if (responseHasContent) hasValidContent = true;
                            }
                            break;
                        default:
                            // Be conservative: treat unknown non-null parts as valid
                            if (part != null)
                                hasValidContent = true;
                            break;
                    }
                    if (hasValidContent) break;
                }
                needsFix = !hasValidContent;
            }
            
            if (needsFix)
            {
                File.AppendAllText(debugLog, $"Message {i} ({message.Role}) needs fixing - Parts are empty or invalid\n");
                
                // Find corresponding history entry more robustly
                ContextEntry? matchingEntry = null;
                // Try index match first
                if (i < history.Count)
                {
                    var candidate = history[i];
                    if (string.Equals(candidate.Role.ToString(), message.Role.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        matchingEntry = candidate;
                    }
                }
                // Fallback by role
                if (matchingEntry == null)
                {
                    if (message.Role == Andy.Llm.Models.MessageRole.User)
                    {
                        matchingEntry = history.LastOrDefault(h => h.Role == Andy.Cli.Services.MessageRole.User);
                    }
                    else if (message.Role == Andy.Llm.Models.MessageRole.Assistant)
                    {
                        // Count how many assistant messages we've seen so far
                        var assistantIndex = request.Messages.Take(i).Count(m => m.Role == Andy.Llm.Models.MessageRole.Assistant);
                        matchingEntry = history.Where(h => h.Role == Andy.Cli.Services.MessageRole.Assistant)
                                              .Skip(assistantIndex)
                                              .FirstOrDefault();
                    }
                }
                
                // Apply the fix using a minimal, valid TextPart only for User/Assistant
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
                    var fallbackText = message.Role == Andy.Llm.Models.MessageRole.User ? "Hello" : "I'm ready to help.";
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
            if (msg.Parts != null && msg.Parts.Count > 0)
            {
                if (msg.Parts[0] is TextPart tp)
                {
                    File.AppendAllText(debugLog, $"    Content: {tp.Text?.Substring(0, Math.Min(30, tp.Text?.Length ?? 0))}...\n");
                }
                else if (msg.Parts[0] is ToolResponsePart trp)
                {
                    var respStr = trp.Response as string ?? (trp.Response != null ? System.Text.Json.JsonSerializer.Serialize(trp.Response) : "");
                    File.AppendAllText(debugLog, $"    ToolResponse: tool={trp.ToolName}, callId={trp.CallId}, len={respStr.Length}\n");
                }
                else if (msg.Parts[0] is ToolCallPart tcp)
                {
                    File.AppendAllText(debugLog, $"    ToolCall: name={tcp.ToolName}, id={tcp.CallId}\n");
                }
            }
        }

        return request;
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
            _logger?.LogWarning(ex, "LLM request failed; will attempt fallback if enabled.");
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
            _pendingFunctionCalls.Clear();

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
                    _pendingFunctionCalls.Add(chunk.FunctionCall);
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
                if (lastUserMessage != null && lastUserMessage.Parts != null)
                {
                    var textPart = lastUserMessage.Parts.OfType<TextPart>().FirstOrDefault();
                    if (textPart != null) prompt = textPart.Text ?? "";
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
            _logger?.LogWarning(ex, "LLM streaming failed; will attempt fallback if enabled.");
            return null;
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

    /// <summary>
    /// Convert tool parameters to JSON Schema format
    /// </summary>
    private Dictionary<string, object> ConvertParametersToSchema(IList<ToolParameter> parameters)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        
        foreach (var param in parameters)
        {
            var propSchema = new Dictionary<string, object>
            {
                ["type"] = ConvertTypeToJsonSchema(param.Type),
                ["description"] = param.Description
            };
            
            if (param.DefaultValue != null)
            {
                propSchema["default"] = param.DefaultValue;
            }
            
            properties[param.Name] = propSchema;
            
            if (param.Required)
            {
                required.Add(param.Name);
            }
        }
        
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        
        if (required.Any())
        {
            schema["required"] = required;
        }
        
        return schema;
    }
    
    private string ConvertTypeToJsonSchema(string dotNetType)
    {
        return dotNetType?.ToLowerInvariant() switch
        {
            "string" => "string",
            "int" or "int32" or "integer" => "integer",
            "long" or "int64" => "integer",
            "bool" or "boolean" => "boolean",
            "double" or "float" or "decimal" => "number",
            "array" or "list" => "array",
            "dictionary" or "object" => "object",
            _ => "string" // Default to string for unknown types
        };
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

    public void Dispose()
    {
        _contentPipeline?.Dispose();
    }
}