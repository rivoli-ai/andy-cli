using Andy.Cli.Services.ContentPipeline;
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
    private readonly ILogger<SimpleAssistantService>? _logger;
    private readonly string _modelName;
    private readonly string _providerName;
    private readonly Dictionary<string, (DateTime startTime, Dictionary<string, object?>? parameters)> _runningTools = new();
    private int _toolCallCounter = 0;
    private int _lastInputTokens = 0;
    private int _lastOutputTokens = 0;

    public SimpleAssistantService(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        string modelName,
        string providerName,
        ILogger<SimpleAssistantService>? logger = null)
    {
        _feed = feed;
        _logger = logger;
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
        var systemPrompt = SystemPrompts.GetDefaultCliPrompt();

        // Wrap the tool executor to update UI when tools execute
        var uiExecutor = new UiUpdatingToolExecutor(toolExecutor, logger as ILogger<UiUpdatingToolExecutor>);

        // Create the SimpleAgent
        _agent = new SimpleAgent(
            llmProvider,
            toolRegistry,
            uiExecutor,  // Use the wrapped executor
            systemPrompt,
            maxTurns: 10,
            logger: logger as ILogger<SimpleAgent>
        );

        // Subscribe to agent events for UI updates
        _agent.ToolCalled += (sender, e) =>
        {
            _logger?.LogInformation("Tool called: {ToolName}", e.ToolName);

            // Use a unique ID for each tool call
            var baseToolId = e.ToolName.ToLower().Replace(" ", "_").Replace("-", "_");
            var toolId = $"{baseToolId}_{++_toolCallCounter}";

            // Track this tool
            _runningTools[toolId] = (DateTime.UtcNow, null);

            // Tell the tracker about this tool so the ToolAdapter can update it
            ToolExecutionTracker.Instance.SetLastActiveToolId(toolId);
            ToolExecutionTracker.Instance.SetFeedView(_feed);

            // Register multiple variations of the tool name to ensure we can find it
            ToolExecutionTracker.Instance.RegisterToolMapping(e.ToolName, toolId);
            ToolExecutionTracker.Instance.RegisterToolMapping(e.ToolName.Replace("-", "_"), toolId);
            ToolExecutionTracker.Instance.RegisterToolMapping(e.ToolName.Replace("_", "-"), toolId);
            ToolExecutionTracker.Instance.RegisterToolMapping(baseToolId, toolId);

            _logger?.LogWarning("[TOOL_CALLED_EVENT] Tool: {ToolName}, ID: {ToolId} - Registered mappings, creating UI",
                e.ToolName, toolId);

            // CREATE UI IMMEDIATELY - parameters will be filled in by ToolAdapter
            var initialParams = new Dictionary<string, object?>
            {
                ["__toolId"] = toolId,
                ["__baseName"] = baseToolId
            };
            _feed.AddToolExecutionStart(toolId, e.ToolName, initialParams);

            // Schedule completion after a reasonable timeout if not completed naturally
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                if (_runningTools.ContainsKey(toolId))
                {
                    var elapsed = DateTime.UtcNow - _runningTools[toolId].startTime;
                    _feed.AddToolExecutionComplete(toolId, true, FormatDuration(elapsed), "Completed");
                    _runningTools.Remove(toolId);
                }
            });
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
            var renderer = new FeedContentRenderer(_feed, _logger as ILogger<FeedContentRenderer>);
            var pipeline = new ContentPipeline.ContentPipeline(processor, sanitizer, renderer, _logger as ILogger<ContentPipeline.ContentPipeline>);

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

            // Show enhanced processing indicator
            _feed.AddProcessingIndicator();

            // Track processing time
            var startTime = DateTime.UtcNow;

            // Estimate input tokens from user message and conversation history
            var history = _agent.GetHistory();
            var contextLength = history.Sum(m => m.Content?.Length ?? 0);
            var userMessageLength = userMessage.Length;
            _lastInputTokens = EstimateTokens(contextLength + userMessageLength);

            // Process message through SimpleAgent
            var result = await _agent.ProcessMessageAsync(userMessage, cancellationToken);

            // Estimate output tokens from response
            _lastOutputTokens = EstimateTokens(result.Response?.Length ?? 0);

            // Complete any running tools
            var toolsToComplete = _runningTools.ToList();
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
                        // Otherwise try to extract from ResultData
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
                        else if (baseToolName.Contains("code_index"))
                        {
                            resultSummary = isSuccess ? "Code repository indexed" : "Failed to index repository";
                        }
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

                _feed.AddToolExecutionComplete(tool.Key, isSuccess, durationStr, resultSummary);
                _runningTools.Remove(tool.Key);
            }

            // Calculate actual duration
            var duration = DateTime.UtcNow - startTime;

            // Clear the processing indicator
            _feed.ClearProcessingIndicator();

            // Add technical summary of what happened
            var technicalSummary = $"Processing completed in {duration.TotalSeconds:F1}s | Model: {_modelName} | Provider: {_providerName}";
            if (!result.Success)
            {
                technicalSummary += $" | Status: Failed - {result.StopReason}";
            }
            _feed.AddMarkdownRich(technicalSummary);
            _feed.AddMarkdownRich(""); // Blank line after technical info

            _logger?.LogInformation("Agent result - Success: {Success}, Response: '{Response}', StopReason: {StopReason}",
                result.Success, result.Response, result.StopReason);

            // Log result details for debugging (not shown to user)
            _logger?.LogDebug("Success={Success}, Response.Length={Length}, StopReason={StopReason}",
                result.Success, result.Response?.Length ?? 0, result.StopReason);

            // Add response to pipeline
            if (!string.IsNullOrEmpty(result.Response))
            {
                pipeline.AddRawContent(result.Response);
            }
            else if (!result.Success)
            {
                // Error case - show the error
                _logger?.LogWarning("Agent failed. StopReason: {StopReason}", result.StopReason);
                pipeline.AddRawContent($"**Error**: Agent failed with StopReason: {result.StopReason}");
            }
            else
            {
                // No response content - this is unexpected
                _logger?.LogWarning("Agent returned empty response. Success: {Success}, StopReason: {StopReason}",
                    result.Success, result.StopReason);
                pipeline.AddRawContent($"[No response received. StopReason: {result.StopReason}]");
            }

            // Show context stats with actual duration
            var stats = GetContextStats();
            pipeline.AddSystemMessage("", SystemMessageType.Context, priority: 1999);
            var contextInfo = $"Context: {stats.TurnCount} turns, ~{stats.EstimatedTokens} tokens, Duration: {duration.TotalSeconds:F1}s";
            pipeline.AddSystemMessage(contextInfo, SystemMessageType.Context, priority: 2000);

            await pipeline.FinalizeAsync();
            pipeline.Dispose();

            return result.Response ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to process message");

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
            LastOutputTokens = _lastOutputTokens
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
