using Andy.Cli.Instrumentation;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Services.Prompts;
using Andy.Cli.Widgets;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// Assistant service using Andy.Engine.SimpleAgent for reliable, direct LLM function calling.
/// </summary>
public class SimpleAssistantService : IDisposable
{
    private readonly SimpleAgent _agent;
    private readonly FeedView _feed;
    private readonly TokenCounter? _tokenCounter;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<SimpleAssistantService>? _logger;
    private readonly string _modelName;
    private readonly string _providerName;
    // Concurrent: mutated from the agent's ToolCalled callback (background agent thread) and read/
    // removed from the end-of-turn completion loop. A plain Dictionary raced across these threads and
    // could corrupt internally, surfacing as a NullReferenceException that aborted the whole turn.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime startTime, Dictionary<string, object?>? parameters)> _runningTools = new();
    // Max agent turns (LLM round-trips, each of which may issue tool calls) per user
    // message. When this is hit the engine stops with StopReason "max_turns_exceeded"
    // and returns a full conversation-history dump as its response (see the guard in
    // ProcessMessageAsync). 10 was far too low for real coding tasks, which routinely
    // need a dozen+ explore/edit round-trips.
    private const int MaxAgentTurns = 50;
    private const string MaxTurnsStopReason = "max_turns_exceeded";

    private int _toolCallCounter = 0;
    private int _lastInputTokens = 0;
    private int _lastOutputTokens = 0;
    // Set once the provider reports real token usage for the current turn; gates the
    // estimate fallback so we never double-count or show char/4 estimates over real numbers.
    private volatile bool _turnHadRealUsage;
    // Live metrics for the in-flight turn, read every frame by the processing indicator.
    private readonly TurnStats _liveStats = new();

    /// <summary>Live metrics (elapsed, operations, tokens) for the in-flight turn.</summary>
    public TurnStats LiveStats => _liveStats;

    /// <summary>
    /// Receives the provider's real token usage for one round-trip (fired by
    /// <see cref="UsageTrackingLlmProvider"/>). Input reflects the current context size sent;
    /// output accumulates across the turn's round-trips. Also feeds the cumulative session
    /// counter so the bottom status bar reflects real billed tokens.
    /// </summary>
    private void OnLlmUsage(Andy.Model.Llm.LlmUsage usage)
    {
        _turnHadRealUsage = true;
        _lastInputTokens = usage.PromptTokens;
        _lastOutputTokens = usage.CompletionTokens;
        _liveStats.SetInputTokens(usage.PromptTokens);
        _liveStats.AddOutputTokens(usage.CompletionTokens);
        _tokenCounter?.AddTokens(usage.PromptTokens, usage.CompletionTokens);
    }

    public SimpleAssistantService(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        string modelName,
        string providerName,
        TokenCounter? tokenCounter = null,
        ILoggerFactory? loggerFactory = null)
    {
        _feed = feed;
        _tokenCounter = tokenCounter;
        // Take an ILoggerFactory so each collaborator gets a correctly-typed logger. Previously a
        // single ILogger<SimpleAssistantService> was passed and `as ILogger<SimpleAgent>` / etc.
        // were used, which always yield null (the generic types are unrelated) - so engine-, tool-,
        // and pipeline-level logs silently went nowhere in the real app.
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<SimpleAssistantService>();
        _modelName = modelName;
        _providerName = providerName;

        // Log initialization details
        _logger?.LogInformation("Initializing SimpleAssistantService with provider: {Provider}, model: {Model}",
            providerName, modelName);

        // Log tool registry information
        var registeredTools = toolRegistry.GetTools();
        var toolNames = registeredTools.Select(t => t.Metadata.Id).ToList();
        _logger?.LogInformation("Tool registry initialized with {ToolCount} tools: {Tools}",
            toolNames.Count, string.Join(", ", toolNames));

        // Build system prompt using the new SystemPrompts helper
        var systemPrompt = Andy.Cli.Services.Prompts.SystemPrompts.GetDefaultCliPrompt();

        // Store system prompt in instrumentation hub for dashboard display
        InstrumentationHub.Instance.SetSystemPrompt(systemPrompt);

        // Wrap the tool executor to update UI when tools execute
        var uiExecutor = new UiUpdatingToolExecutor(toolExecutor, loggerFactory?.CreateLogger<UiUpdatingToolExecutor>());

        // Wrap the LLM provider so each round-trip's REAL token usage flows into the live turn
        // stats (thinking row) and the session token counter, replacing char/4 estimates.
        var usageTrackingProvider = new UsageTrackingLlmProvider(
            llmProvider, OnLlmUsage, loggerFactory?.CreateLogger<UsageTrackingLlmProvider>());

        // Create the SimpleAgent
        _agent = new SimpleAgent(
            usageTrackingProvider,
            toolRegistry,
            uiExecutor,  // Use the wrapped executor
            systemPrompt,
            maxTurns: MaxAgentTurns,
            logger: loggerFactory?.CreateLogger<SimpleAgent>()
        );

        // Subscribe to agent events for UI updates
        _agent.ToolCalled += (sender, e) =>
        {
            _logger?.LogInformation("Tool called: {ToolName}", e.ToolName);

            // Count this operation against the live turn stats (drives the thinking-row counter).
            _liveStats.IncrementOperations();

            // Use a unique ID for each tool call
            var baseToolId = e.ToolName.ToLower().Replace(" ", "_").Replace("-", "_");
            var toolId = $"{baseToolId}_{++_toolCallCounter}";

            // Generate a unique correlation ID for this specific tool execution
            var correlationId = Guid.NewGuid().ToString("N")[..12]; // Longer ID for uniqueness

            // Track this tool
            _runningTools[toolId] = (DateTime.UtcNow, null);

            // INSTRUMENTATION: Publish tool call event
            var toolCallEvent = new ToolCallEvent
            {
                ToolName = e.ToolName,
                ToolId = toolId,
                Parameters = new Dictionary<string, object?>() // Will be populated by UiUpdatingToolExecutor
            };
            InstrumentationHub.Instance.Publish(toolCallEvent);

            // Tell the tracker about this tool so the ToolAdapter can update it
            ToolExecutionTracker.Instance.SetLastActiveToolId(toolId);
            ToolExecutionTracker.Instance.SetFeedView(_feed);

            // IMPORTANT: Enqueue this tool execution so UiUpdatingToolExecutor can claim it
            // This ensures the correct UI tool ID is used even with parallel executions
            ToolExecutionTracker.Instance.EnqueuePendingTool(e.ToolName, toolId);

            // Register the correlation ID mapping (in case the agent uses it)
            ToolExecutionTracker.Instance.RegisterCorrelationMapping(correlationId, toolId);

            // Also register the correlation ID as a tool name mapping (for fallback)
            ToolExecutionTracker.Instance.RegisterToolMapping(correlationId, toolId);

            // Register multiple variations of the tool name to ensure we can find it (fallback)
            ToolExecutionTracker.Instance.RegisterToolMapping(e.ToolName, toolId);
            ToolExecutionTracker.Instance.RegisterToolMapping(e.ToolName.Replace("-", "_"), toolId);
            ToolExecutionTracker.Instance.RegisterToolMapping(e.ToolName.Replace("_", "-"), toolId);
            ToolExecutionTracker.Instance.RegisterToolMapping(baseToolId, toolId);

            _logger?.LogWarning("[TOOL_CALLED_EVENT] Tool: {ToolName}, ID: {ToolId}, Correlation: {CorrelationId} - Registered mappings, creating UI",
                e.ToolName, toolId, correlationId);

            // CREATE UI IMMEDIATELY - parameters will be filled in by ToolAdapter
            var initialParams = new Dictionary<string, object?>
            {
                ["__toolId"] = toolId,
                ["__baseName"] = baseToolId
            };
            _feed.AddToolExecutionStart(toolId, e.ToolName, initialParams);

            // The spinner is stopped by UiUpdatingToolExecutor the instant the tool returns, and the
            // end-of-turn loop below is the backstop for anything it missed. A previous 30s background
            // timer here both spuriously marked still-running tools "Completed" and, by mutating
            // _runningTools from a stray thread pool task (often one left over from a prior turn), raced
            // the dictionary into the NullReferenceException that crashed the turn.
        };
    }

    /// <summary>
    /// Process a user message
    /// </summary>
    public async Task<string> ProcessMessageAsync(
        string userMessage,
        bool enableStreaming = false, // Ignored for now - streaming not yet implemented
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Create new content pipeline for this request
            var processor = new MarkdownContentProcessor();
            var sanitizer = new TextContentSanitizer();
            var renderer = new FeedContentRenderer(_feed, _loggerFactory?.CreateLogger<FeedContentRenderer>());
            var pipeline = new ContentPipeline.ContentPipeline(processor, sanitizer, renderer, _loggerFactory?.CreateLogger<ContentPipeline.ContentPipeline>());

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

            // Track processing time and begin live turn metrics before showing the indicator,
            // so the thinking row has a valid clock/counters from its first frame.
            var startTime = DateTime.UtcNow;
            _liveStats.Begin(startTime);
            _turnHadRealUsage = false;

            // Show processing indicator (renders live elapsed/operations/context stats) while
            // waiting for the LLM response.
            _feed.AddProcessingIndicator(_liveStats);

            // Estimate input tokens from user message and conversation history
            var history = _agent.GetHistory();
            var contextLength = history.Sum(m => m.Content?.Length ?? 0);
            var userMessageLength = userMessage.Length;
            _lastInputTokens = EstimateTokens(contextLength + userMessageLength);

            // Seed the live stats with an input estimate so the thinking row shows context size
            // immediately; real per-round-trip usage (OnLlmUsage) overwrites it once the first LLM
            // response arrives. The session token counter is updated from real usage only (or an
            // estimate fallback at end of turn) to avoid double-counting.
            _liveStats.SetInputTokens(_lastInputTokens);

            // INSTRUMENTATION: Publish LLM request event
            var requestEvent = new LlmRequestEvent
            {
                Provider = _providerName,
                Model = _modelName,
                UserMessage = userMessage,
                ConversationTurns = history.Count / 2,
                EstimatedInputTokens = _lastInputTokens,
                ConversationHistory = history.Select(m => new MessageSummary
                {
                    Role = m.Role.ToString(),
                    Length = m.Content?.Length ?? 0,
                    Preview = m.Content?.Length > 100 ? m.Content.Substring(0, 100) + "..." : m.Content ?? "",
                    HasToolCalls = m.ToolCalls?.Any() ?? false,
                    ToolCallCount = m.ToolCalls?.Count ?? 0
                }).ToList()
            };
            InstrumentationHub.Instance.Publish(requestEvent);

            // Process message through SimpleAgent
            var llmStartTime = DateTime.UtcNow;
            var result = await _agent.ProcessMessageAsync(userMessage, cancellationToken);
            var llmDuration = DateTime.UtcNow - llmStartTime;

            // INSTRUMENTATION: Publish LLM response event
            var responseEvent = new LlmResponseEvent
            {
                RequestId = requestEvent.EventId,
                Success = result.Success,
                StopReason = result.StopReason,
                Response = result.Response,
                ResponseLength = result.Response?.Length ?? 0,
                EstimatedOutputTokens = EstimateTokens(result.Response?.Length ?? 0),
                Duration = llmDuration,
                ActualInputTokens = null, // Not available from SimpleAgent yet
                ActualOutputTokens = null
            };
            InstrumentationHub.Instance.Publish(responseEvent);

            // If the provider reported real usage during the turn, the live stats and session
            // counter were already updated per round-trip via OnLlmUsage. Only fall back to a
            // char/4 estimate when no usage was available (e.g. a provider that omits it), so the
            // counters still move rather than freezing at zero.
            if (!_turnHadRealUsage)
            {
                _lastOutputTokens = EstimateTokens(result.Response?.Length ?? 0);
                _tokenCounter?.AddTokens(_lastInputTokens, _lastOutputTokens);
                _liveStats.SetInputTokens(_lastInputTokens);
                _liveStats.SetOutputTokens(_lastOutputTokens);
            }

            // Complete any running tools
            var toolsToComplete = _runningTools.ToList();
            var toolsWereExecuted = toolsToComplete.Count > 0;
            foreach (var tool in toolsToComplete)
            {
                var elapsed = DateTime.UtcNow - tool.Value.startTime;
                var durationStr = FormatDuration(elapsed);

                // Wait a bit to ensure tool execution has completed and been tracked
                await Task.Delay(200);

                _logger?.LogInformation("[TOOL_COMPLETE] Looking for execution info for toolId: {ToolId}", tool.Key);

                // Try to get actual execution info from tracker
                var baseToolName = tool.Key.Contains('_') ?
                    tool.Key.Substring(0, tool.Key.LastIndexOf('_')) :
                    tool.Key;

                // Try multiple ways to find the execution info
                var executionInfo = ToolExecutionTracker.Instance.GetExecutionInfo(tool.Key) ??
                                   ToolExecutionTracker.Instance.GetExecutionInfo(baseToolName);

                string? resultSummary = null;
                bool isSuccess = true; // Assume success unless we know otherwise

                if (executionInfo != null)
                {
                    _logger?.LogInformation("[TOOL_COMPLETE] Found execution info - Success: {Success}, Result: {Result}, Params: {ParamCount}",
                        executionInfo.Success, executionInfo.Result, executionInfo.Parameters?.Count ?? 0);

                    // Check if the tool execution was successful
                    isSuccess = executionInfo.Success;

                    // For datetime_tool, show the actual output
                    if (baseToolName.Contains("datetime"))
                    {
                        // Use the Result directly if it was set by UiUpdatingToolExecutor
                        if (!string.IsNullOrEmpty(executionInfo.Result))
                        {
                            resultSummary = executionInfo.Result;
                        }
                        // Check if ResultData is an anonymous type (common for datetime results)
                        else if (executionInfo.ResultData != null && executionInfo.ResultData.GetType().Name.Contains("AnonymousType"))
                        {
                            // Try to extract formatted field from anonymous type using reflection
                            var resultType = executionInfo.ResultData.GetType();
                            foreach (var propName in new[] { "formatted", "output", "result", "value" })
                            {
                                var prop = resultType.GetProperty(propName);
                                if (prop != null)
                                {
                                    var value = prop.GetValue(executionInfo.ResultData);
                                    if (value != null)
                                    {
                                        resultSummary = value.ToString();
                                        _logger?.LogInformation("[TOOL_COMPLETE] Extracted '{PropName}' from anonymous type: {Value}",
                                            propName, resultSummary);
                                        break;
                                    }
                                }
                            }
                        }
                        // Otherwise try to extract from ResultData if it's a Dictionary
                        else if (executionInfo.ResultData is Dictionary<string, object?> resultDict)
                        {
                            if (resultDict.TryGetValue("result", out var res) && res != null)
                            {
                                resultSummary = res.ToString();
                            }
                            else if (resultDict.TryGetValue("output", out var output) && output != null)
                            {
                                resultSummary = output.ToString();
                            }
                            else if (resultDict.TryGetValue("formatted", out var formatted) && formatted != null)
                            {
                                resultSummary = formatted.ToString();
                            }
                        }
                    }
                    // Use the actual result from the tool execution
                    else if (!string.IsNullOrEmpty(executionInfo.Result))
                    {
                        resultSummary = executionInfo.Result;
                    }
                    else if (executionInfo.Parameters != null)
                    {
                        // Try to create a meaningful summary from parameters
                        if (baseToolName.Contains("read_file") &&
                            executionInfo.Parameters.TryGetValue("file_path", out var filePath))
                        {
                            var fileName = Path.GetFileName(filePath?.ToString() ?? "");
                            resultSummary = isSuccess ? $"Read {fileName}" : $"Failed to read {fileName}";
                        }
                        else if (baseToolName.Contains("list_directory"))
                        {
                            // Try both parameter names (path and directory_path)
                            object? dirPath = null;
                            if (!executionInfo.Parameters.TryGetValue("directory_path", out dirPath))
                                executionInfo.Parameters.TryGetValue("path", out dirPath);

                            if (dirPath != null)
                            {
                                var dirStr = dirPath.ToString() ?? ".";
                                resultSummary = isSuccess ? $"Listed {dirStr}" : $"Directory not found: {dirStr}";
                            }
                            else
                            {
                                resultSummary = isSuccess ? "Directory listed" : "Directory operation failed";
                            }
                        }
                        // code_index tool result is now handled by UiUpdatingToolExecutor and available in executionInfo.Result
                    }
                }

                // Fallback to generic message if no actual result available
                if (string.IsNullOrEmpty(resultSummary))
                {
                    // One more try to get any execution info by searching all executions
                    var anyExecution = ToolExecutionTracker.Instance.GetExecutionInfo(tool.Key);
                    if (anyExecution != null && anyExecution.Parameters != null)
                    {
                        isSuccess = anyExecution.Success;

                        if (baseToolName.Contains("list_directory"))
                        {
                            // Try both parameter names
                            object? dirPath = null;
                            if (!anyExecution.Parameters.TryGetValue("directory_path", out dirPath))
                                anyExecution.Parameters.TryGetValue("path", out dirPath);

                            if (dirPath != null)
                            {
                                resultSummary = isSuccess ? $"Listed {dirPath}" : $"Directory not found: {dirPath}";
                            }
                        }
                        else if (baseToolName.Contains("read_file") &&
                                anyExecution.Parameters.TryGetValue("file_path", out var filePath))
                        {
                            var fileName = Path.GetFileName(filePath?.ToString() ?? "");
                            resultSummary = isSuccess ? $"Read {fileName}" : $"Failed to read {fileName}";
                        }
                    }

                    // Final fallback - don't show generic messages, they're annoying
                    if (string.IsNullOrEmpty(resultSummary))
                    {
                        _logger?.LogWarning("[TOOL_COMPLETE] No execution info found for {ToolName}", baseToolName);

                        // DEBUG: Write to file what's happening
                        try
                        {
                            var debugInfo = $"[{DateTime.Now:HH:mm:ss.fff}] NO RESULT for {baseToolName}:\n";
                            debugInfo += $"  executionInfo was null: {executionInfo == null}\n";
                            if (executionInfo != null)
                            {
                                debugInfo += $"  Result: '{executionInfo.Result}'\n";
                                debugInfo += $"  ResultData type: {executionInfo.ResultData?.GetType().Name ?? "null"}\n";
                            }
                            System.IO.File.AppendAllText("/tmp/tool_complete_debug.txt", debugInfo + "\n");
                        }
                        catch { }

                        // Don't show anything generic - leave it empty or show the actual status
                        resultSummary = isSuccess ? "" : "Failed";
                    }
                }

                // DEBUG: Log what we're passing to the UI
                _logger?.LogWarning("[TOOL_COMPLETE] Calling AddToolExecutionComplete for {ToolId} with result: '{Result}'",
                    tool.Key, resultSummary);

                // INSTRUMENTATION: Publish tool complete event
                var toolCompleteEvent = new ToolCompleteEvent
                {
                    ToolName = baseToolName,
                    ToolId = tool.Key,
                    Success = isSuccess,
                    Result = resultSummary,
                    ResultData = executionInfo?.ResultData,
                    Duration = elapsed
                };
                InstrumentationHub.Instance.Publish(toolCompleteEvent);

                _feed.AddToolExecutionComplete(tool.Key, isSuccess, durationStr, resultSummary);
                _runningTools.TryRemove(tool.Key, out _);
            }

            // Calculate actual duration
            var duration = DateTime.UtcNow - startTime;

            // Clear the processing indicator (has 1 blank line built-in)
            _liveStats.End();
            _feed.ClearProcessingIndicator();

            // Processing completed message disabled to avoid rendering issues
            // var technicalSummary = $"Processing completed in {duration.TotalSeconds:F1}s | Model: {_modelName} | Provider: {_providerName}";
            // if (!result.Success)
            // {
            //     technicalSummary += $" | Status: Failed - {result.StopReason}";
            // }
            // _feed.AddMarkdownRich(Commands.ConsoleColors.Dim(technicalSummary));
            // _feed.AddMarkdownRich("");

            _logger?.LogInformation("Agent result - Success: {Success}, Response: '{Response}', StopReason: {StopReason}",
                result.Success, result.Response, result.StopReason);

            // Log result details for debugging (not shown to user)
            _logger?.LogDebug("Success={Success}, Response.Length={Length}, StopReason={StopReason}",
                result.Success, result.Response?.Length ?? 0, result.StopReason);

            // Add blank line after tools if they were executed
            // Use SpacerItem since both pipeline and AddMarkdownRich filter out empty content
            if (toolsWereExecuted)
            {
                _feed.AddItem(new Andy.Cli.Widgets.SpacerItem(1));
            }

            // Add response to pipeline. On the turn-limit path the engine returns the FULL
            // conversation history (raw tool payloads, embedded CRLFs and all) as the response;
            // SelectResponseContent replaces that with a concise notice so it doesn't flood the feed.
            if (IsMaxTurnsExceeded(result.StopReason))
            {
                _logger?.LogWarning("Agent hit the {MaxTurns}-turn limit; suppressing history dump", MaxAgentTurns);
            }
            else if (!result.Success)
            {
                _logger?.LogWarning("Agent failed. StopReason: {StopReason}", result.StopReason);
            }
            else if (string.IsNullOrEmpty(result.Response))
            {
                _logger?.LogWarning("Agent returned empty response. Success: {Success}, StopReason: {StopReason}",
                    result.Success, result.StopReason);
            }
            pipeline.AddRawContent(SelectResponseContent(result.Response, result.Success, result.StopReason));

            // Context line disabled to avoid rendering issues
            // pipeline.AddSystemMessage("", SystemMessageType.Context, priority: 1999);
            // var stats = GetContextStats();
            // var contextInfo = Commands.ConsoleColors.Metadata($"Context: {stats.TurnCount} turns, ~{stats.EstimatedTokens} tokens, Duration: {duration.TotalSeconds:F1}s");
            // pipeline.AddSystemMessage(contextInfo, SystemMessageType.Context, priority: 2000);

            await pipeline.FinalizeAsync();
            pipeline.Dispose();

            // Don't propagate the raw history dump as the return value either.
            if (IsMaxTurnsExceeded(result.StopReason))
            {
                return $"[Reached the {MaxAgentTurns}-turn tool-call limit before completing this request.]";
            }

            return result.Response ?? string.Empty;
        }
        catch (Exception ex)
        {
            // Clear the processing indicator if an error occurred
            _liveStats.End();
            _feed.ClearProcessingIndicator();

            _logger?.LogError(ex, "Failed to process message");
            CrashLog.Write("SimpleAssistantService.ProcessMessageAsync", ex);

            // Log full error details for debugging
            if (ex.Message.Contains("Cerebras") || _providerName.Contains("cerebras", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError("Cerebras provider error - Message: {Message}", ex.Message);
                _logger?.LogError("Cerebras provider error - Inner: {Inner}", ex.InnerException?.Message);
                _logger?.LogError("Cerebras provider error - Stack: {Stack}", ex.StackTrace);
            }

            _feed.AddMarkdownRich($"[ERROR] {ex.Message}");

            // Show more details for provider-specific errors
            if (_providerName.Contains("cerebras", StringComparison.OrdinalIgnoreCase))
            {
                _feed.AddMarkdownRich($"        Provider: {_providerName}");
                _feed.AddMarkdownRich($"        Model: {_modelName}");
                if (ex.InnerException != null)
                {
                    _feed.AddMarkdownRich($"        Details: {ex.InnerException.Message}");
                }
            }

            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Clear the conversation context
    /// </summary>
    public void ClearContext()
    {
        _agent.ClearHistory();
        _lastInputTokens = 0;
        _lastOutputTokens = 0;
        _logger?.LogInformation("Conversation context cleared");
    }

    public class ContextStats
    {
        public int TurnCount { get; set; }
        public int EstimatedTokens { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public int LastInputTokens { get; set; }
        public int LastOutputTokens { get; set; }
        public string ModelName { get; set; } = string.Empty;
        public string ProviderName { get; set; } = string.Empty;
        /// <summary>Model context window in tokens, or 0 when unknown.</summary>
        public int MaxContextTokens { get; set; }
    }

    /// <summary>
    /// Get context statistics from the agent
    /// </summary>
    public ContextStats GetContextStats()
    {
        var history = _agent.GetHistory();
        var turnCount = history.Count / 2; // Rough estimate: user + assistant per turn

        // Rough token estimation
        var estimatedTokens = history.Sum(m => m.Content?.Length ?? 0) / 4; // Simple char-to-token ratio

        return new ContextStats
        {
            TurnCount = turnCount,
            EstimatedTokens = estimatedTokens,
            TotalDuration = TimeSpan.Zero, // Duration tracked per-message in SimpleAgent
            LastInputTokens = _lastInputTokens,
            LastOutputTokens = _lastOutputTokens,
            ModelName = _modelName,
            ProviderName = _providerName,
            MaxContextTokens = ContextWindow.GetMaxTokens(_modelName, _providerName)
        };
    }

    /// <summary>
    /// Estimate token count from text length.
    /// Uses a conservative ratio of 4 characters per token (typical for English text).
    /// </summary>
    private static int EstimateTokens(int characterCount)
    {
        // Conservative estimate: 1 token ≈ 4 characters
        return Math.Max(1, characterCount / 4);
    }

    /// <summary>True when the agent stopped because it exhausted its turn budget.</summary>
    internal static bool IsMaxTurnsExceeded(string? stopReason) =>
        string.Equals(stopReason, MaxTurnsStopReason, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decide what to render for an agent result. On the turn-limit path the engine packs the
    /// entire conversation history into the response; we replace it with a short notice so the
    /// feed isn't flooded with raw tool JSON and CRLFs.
    /// </summary>
    internal static string SelectResponseContent(string? response, bool success, string? stopReason)
    {
        if (IsMaxTurnsExceeded(stopReason))
        {
            return $"**Reached the {MaxAgentTurns}-turn tool-call limit** before completing this request. " +
                   "Partial work above may be incomplete - try narrowing the task or asking it to continue.";
        }
        if (!string.IsNullOrEmpty(response))
        {
            return response;
        }
        if (!success)
        {
            return $"**Error**: Agent failed with StopReason: {stopReason}";
        }
        return $"[No response received. StopReason: {stopReason}]";
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds < 1000)
            return $"{elapsed.TotalMilliseconds:F0}ms";
        else if (elapsed.TotalSeconds < 60)
            return $"{elapsed.TotalSeconds:F1}s";
        else
            return $"{elapsed.TotalMinutes:F1}m";
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
        _agent?.Dispose();
    }
}
