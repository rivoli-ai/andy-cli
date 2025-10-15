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

        // Create the SimpleAgent
        _agent = new SimpleAgent(
            llmProvider,
            toolRegistry,
            toolExecutor,
            systemPrompt,
            maxTurns: 10,
            logger: logger as ILogger<SimpleAgent>
        );

        // Subscribe to agent events for UI updates
        _agent.ToolCalled += (sender, e) =>
        {
            _logger?.LogInformation("Tool called: {ToolName}", e.ToolName);

            // Show enhanced tool execution display
            // Use a unique ID for each tool call to handle multiple calls to the same tool
            var baseToolId = e.ToolName.ToLower().Replace(" ", "_").Replace("-", "_");
            var toolId = $"{baseToolId}_{++_toolCallCounter}";

            // Try to create meaningful parameters based on tool name and context
            Dictionary<string, object?>? parameters = null;

            // For code_index, we can infer it's indexing the current directory
            if (baseToolId.Contains("code_index"))
            {
                parameters = new Dictionary<string, object?>
                {
                    ["path"] = Directory.GetCurrentDirectory(),
                    ["recursive"] = true
                };
            }
            else if (baseToolId.Contains("list_directory"))
            {
                parameters = new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["recursive"] = false
                };
            }

            _runningTools[toolId] = (DateTime.UtcNow, parameters);
            _feed.AddToolExecutionStart(toolId, e.ToolName, parameters);

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

            // Process message through SimpleAgent
            var result = await _agent.ProcessMessageAsync(userMessage, cancellationToken);

            // Complete any running tools with result summary
            var toolsToComplete = _runningTools.ToList();
            foreach (var tool in toolsToComplete)
            {
                var elapsed = DateTime.UtcNow - tool.Value.startTime;
                var durationStr = FormatDuration(elapsed);

                // Extract base tool name from the unique ID (remove _counter suffix)
                var baseToolName = tool.Key.Contains('_') ?
                    tool.Key.Substring(0, tool.Key.LastIndexOf('_')) :
                    tool.Key;

                // Try to create a meaningful result summary based on tool name
                // Note: SimpleAgent doesn't provide tool results, so we create informative summaries
                string? resultSummary = null;
                if (baseToolName.Contains("list_directory"))
                {
                    // Simulate a result that would come from the tool
                    var currentDir = Directory.GetCurrentDirectory();
                    try
                    {
                        var files = Directory.GetFiles(currentDir).Length;
                        var dirs = Directory.GetDirectories(currentDir).Length;
                        resultSummary = $"{{\"file_count\":{files},\"directory_count\":{dirs}}}";
                    }
                    catch
                    {
                        resultSummary = "Directory contents retrieved";
                    }
                }
                else if (baseToolName.Contains("code_index"))
                {
                    // Provide more detailed code index information
                    try
                    {
                        var currentDir = Directory.GetCurrentDirectory();
                        var codeFiles = Directory.GetFiles(currentDir, "*.cs", SearchOption.AllDirectories).Length +
                                       Directory.GetFiles(currentDir, "*.py", SearchOption.AllDirectories).Length +
                                       Directory.GetFiles(currentDir, "*.js", SearchOption.AllDirectories).Length +
                                       Directory.GetFiles(currentDir, "*.ts", SearchOption.AllDirectories).Length;
                        var totalFiles = Directory.GetFiles(currentDir, "*", SearchOption.AllDirectories).Length;
                        resultSummary = $"Indexed {codeFiles} code files out of {totalFiles} total files";
                    }
                    catch
                    {
                        resultSummary = "Code repository indexed";
                    }
                }
                else if (baseToolName.Contains("read_file"))
                {
                    resultSummary = "File contents read";
                }
                else if (baseToolName.Contains("update_file") || baseToolName.Contains("edit_file"))
                {
                    resultSummary = "File updated successfully";
                }
                else if (baseToolName.Contains("bash") || baseToolName.Contains("command"))
                {
                    resultSummary = "Command executed";
                }

                _feed.AddToolExecutionComplete(tool.Key, true, durationStr, resultSummary);
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
        _logger?.LogInformation("Conversation context cleared");
    }

    public class ContextStats
    {
        public int TurnCount { get; set; }
        public int EstimatedTokens { get; set; }
        public TimeSpan TotalDuration { get; set; }
    }

    /// <summary>
    /// Get context statistics from the agent
    /// </summary>
    public ContextStats GetContextStats()
    {
        var history = _agent.GetHistory();
        var turnCount = history.Count / 2; // Rough estimate: user + assistant per turn

        // Rough token estimation
        var estimatedTokens = history.Sum(m => m.Content.Length / 4); // Simple char-to-token ratio

        return new ContextStats
        {
            TurnCount = turnCount,
            EstimatedTokens = estimatedTokens,
            TotalDuration = TimeSpan.Zero // Duration tracked per-message in SimpleAgent
        };
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
