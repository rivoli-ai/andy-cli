using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Andy.Engine;
using Andy.Engine.Contracts;
using Andy.Engine.Interactive;
using Andy.Engine.Planner;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Andy.Cli.Services;

/// <summary>
/// Conversation service using Andy.Engine.InteractiveAgent
/// Replaces the old AssistantService that used Andy.Model.Orchestration
/// </summary>
public class EngineAssistantService : IDisposable
{
    private readonly Agent _agent;
    private readonly InteractiveAgent _interactiveAgent;
    private readonly FeedView _feed;
    private readonly ILogger<EngineAssistantService>? _logger;
    private readonly string _modelName;
    private readonly string _providerName;
    private readonly string _systemPrompt;
    private bool _isFirstMessage = true;
    private readonly CumulativeOutputTracker _outputTracker = new();
    private string? _lastUserQuestion = null;

    public EngineAssistantService(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        string systemPrompt,
        ILogger<EngineAssistantService>? logger = null,
        string modelName = "",
        string providerName = "")
    {
        _feed = feed;
        _logger = logger;
        _modelName = modelName;
        _providerName = providerName;
        _systemPrompt = systemPrompt;

        // Log initialization details
        _logger?.LogInformation("Initializing EngineAssistantService with provider: {Provider}, model: {Model}",
            providerName, modelName);

        // Log tool registry information
        var registeredTools = toolRegistry.GetTools();
        var toolNames = registeredTools.Select(t => t.Metadata.Id).ToList();
        _logger?.LogInformation("Tool registry initialized with {ToolCount} tools: {Tools}",
            toolNames.Count, string.Join(", ", toolNames));

        // Show tool count in UI for visibility
        _feed.AddMarkdownRich($"[INFO] Loaded {toolNames.Count} tools for agent");

        // Build the core agent using andy-engine
        var agentBuilder = AgentBuilder.Create()
            .WithDefaults(llmProvider, toolRegistry, toolExecutor)
            .WithPlannerOptions(new PlannerOptions
            {
                Temperature = 0.0, // Deterministic for CLI usage
                MaxTokens = 4096
            });

        if (logger != null)
        {
            var agentLogger = logger as ILogger<Agent>;
            if (agentLogger != null)
            {
                agentBuilder = agentBuilder.WithLogger(agentLogger);
            }
        }

        _agent = agentBuilder.Build();

        // Create user interface adapter for FeedView
        var userInterface = new FeedUserInterface(feed, logger as ILogger<FeedUserInterface>);

        // Build the interactive agent wrapper
        var interactiveBuilder = InteractiveAgentBuilder.Create()
            .WithDefaults(llmProvider, toolRegistry, toolExecutor)
            .WithUserInterface(userInterface)
            .WithOptions(new InteractiveAgentOptions
            {
                DefaultBudget = new Budget(MaxTurns: 10, MaxWallClock: TimeSpan.FromMinutes(5)),
                ConversationOptions = new ConversationOptions
                {
                    MaxHistoryTurns = 100,
                    SummaryTurnCount = 5
                },
                ShowInitialHelp = false,
                WelcomeMessage = "",
                GoodbyeMessage = ""
            });

        if (logger != null)
        {
            var interactiveLogger = logger as ILogger<InteractiveAgent>;
            var agentLogger = logger as ILogger<Agent>;

            if (interactiveLogger != null && agentLogger != null)
            {
                interactiveBuilder = interactiveBuilder
                    .WithLogger(interactiveLogger)
                    .WithAgentLogger(agentLogger);
            }
        }

        _interactiveAgent = interactiveBuilder.Build();

        // Subscribe to agent events for UI updates
        SubscribeToEvents();
    }

    private void SubscribeToEvents()
    {
        // Agent turn lifecycle events (internal to each agent execution)
        // Note: Agent turns reset for each message - they're not conversation turns
        _agent.TurnStarted += (sender, e) =>
        {
            _logger?.LogInformation("Agent turn {TurnNumber} started for trace {TraceId}",
                e.TurnNumber, e.TraceId);
            // Don't show internal agent turns to user - confusing
        };

        _agent.TurnCompleted += (sender, e) =>
        {
            _logger?.LogInformation("Agent turn {TurnNumber} completed: {ActionType}",
                e.TurnNumber, e.ActionType);
            // Don't show internal agent action types - confusing
        };

        // Tool execution events
        _agent.ToolCalled += (sender, e) =>
        {
            _logger?.LogInformation("Tool called: {ToolName}", e.ToolName);
            _feed.AddMarkdownRich($"[TOOL] Executing: {e.ToolName}");
        };

        // User input requests - capture the question for display
        _agent.UserInputRequested += (sender, e) =>
        {
            _lastUserQuestion = e.Question;
            _logger?.LogInformation("Agent requested user input: {Question}", e.Question);
            // Question will be displayed when processing the result
        };
    }

    /// <summary>
    /// Process a user message
    /// </summary>
    public async Task<string> ProcessMessageAsync(
        string userMessage,
        bool enableStreaming = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Reset output tracker for new message
            _outputTracker.Reset();

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

            // Include system prompt with first message
            string messageToSend = userMessage;
            if (_isFirstMessage && !string.IsNullOrWhiteSpace(_systemPrompt))
            {
                // Prepend system prompt to the first user message
                messageToSend = $"{_systemPrompt}\n\n{userMessage}";
                _isFirstMessage = false;
            }

            // Show loading indicator
            _feed.AddMarkdownRich($"[...] Processing request...");

            // Process message through interactive agent
            var result = await _interactiveAgent.ProcessMessageAsync(messageToSend, cancellationToken);

            // Extract response from agent result
            string responseContent = string.Empty;

            // Log result details for debugging
            _logger?.LogInformation("Agent result - Success: {Success}, StopReason: {StopReason}, HasObservation: {HasObs}",
                result.Success, result.StopReason, result.FinalState.LastObservation != null);

            if (result.Success)
            {
                // Get the final observation from the agent
                var observation = result.FinalState.LastObservation;
                if (observation != null && !string.IsNullOrEmpty(observation.Summary))
                {
                    responseContent = observation.Summary;
                    pipeline.AddRawContent(responseContent);
                }

                // If no observation summary, show the stop reason (which contains the agent's response)
                if (string.IsNullOrEmpty(responseContent) && !string.IsNullOrEmpty(result.StopReason))
                {
                    responseContent = result.StopReason;
                    pipeline.AddRawContent(responseContent);
                }

                // Show key facts if available
                if (observation?.KeyFacts.Count > 0)
                {
                    var factsText = new StringBuilder();
                    factsText.AppendLine("\n**Key Information:**");
                    foreach (var (key, value) in observation.KeyFacts)
                    {
                        factsText.AppendLine($"- **{key}**: {value}");
                    }
                    pipeline.AddRawContent(factsText.ToString());
                }

                // If still no content, provide a generic success message
                if (string.IsNullOrEmpty(responseContent))
                {
                    responseContent = "Task completed successfully.";
                    pipeline.AddRawContent(responseContent);
                }
            }
            else
            {
                // Check if this is a user input request
                if (result.StopReason == "User input required")
                {
                    // Agent needs clarification - show the question if we captured it
                    if (!string.IsNullOrEmpty(_lastUserQuestion))
                    {
                        responseContent = _lastUserQuestion;
                        pipeline.AddRawContent(responseContent);
                        _lastUserQuestion = null; // Clear for next time
                    }
                    else
                    {
                        // Fallback: extract from working memory digest if available
                        var workingMemory = result.FinalState.WorkingMemoryDigest;
                        if (workingMemory.TryGetValue("user_query", out var query))
                        {
                            responseContent = query;
                            pipeline.AddRawContent(responseContent);
                        }
                        else
                        {
                            // No question captured - show diagnostic info
                            responseContent = "The agent needs more information but didn't specify what.";
                            pipeline.AddRawContent(responseContent);

                            // Add diagnostic information
                            var diagnosticInfo = $"\n**Debug Info:**\n- Stop Reason: {result.StopReason}\n- Working Memory Keys: {string.Join(", ", workingMemory.Keys)}";
                            pipeline.AddRawContent(diagnosticInfo);

                            _logger?.LogWarning("User input required but no question was captured. WorkingMemory: {Memory}",
                                string.Join(", ", workingMemory.Select(kv => $"{kv.Key}={kv.Value}")));
                        }
                    }
                }
                // Check if this is a conversational response (StopReason contains the response)
                // For chat-style interactions, the agent "stops" with the response content
                else if (!string.IsNullOrEmpty(result.StopReason) && !result.StopReason.StartsWith("Error"))
                {
                    // This is a normal chat response, not an error
                    responseContent = result.StopReason;
                    pipeline.AddRawContent(responseContent);
                }
                else
                {
                    // Actual error/failure
                    var errorMsg = $"Task failed: {result.StopReason}";
                    _logger?.LogWarning("Agent task failed: {Reason}", result.StopReason);
                    pipeline.AddRawContent(errorMsg);
                    responseContent = errorMsg;
                }
            }

            // Show context stats
            var stats = GetContextStats();
            pipeline.AddSystemMessage("", SystemMessageType.Context, priority: 1999);
            var contextInfo = $"Context: {stats.TurnCount} turns, ~{stats.EstimatedTokens} tokens, Duration: {stats.TotalDuration.TotalSeconds:F1}s";
            pipeline.AddSystemMessage(contextInfo, SystemMessageType.Context, priority: 2000);

            await pipeline.FinalizeAsync();
            pipeline.Dispose();

            return responseContent;
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
    /// Update the current model and provider information
    /// </summary>
    public void UpdateModelInfo(string modelName, string providerName)
    {
        _logger?.LogInformation("Model info updated: {Model} / {Provider}", modelName, providerName);
    }

    /// <summary>
    /// Clear the conversation context
    /// </summary>
    public void ClearContext()
    {
        _outputTracker.Reset();
        // TODO: Clear interactive agent conversation history
        // The InteractiveAgent has a ClearConversationAsync() method we could call
    }

    public class ContextStats
    {
        public int TurnCount { get; set; }
        public int EstimatedTokens { get; set; }
        public TimeSpan TotalDuration { get; set; }
    }

    /// <summary>
    /// Get context statistics from the interactive agent
    /// </summary>
    public ContextStats GetContextStats()
    {
        var history = _interactiveAgent.History;
        var turnCount = history.Count;

        // Rough token estimation (could be improved)
        var estimatedTokens = turnCount * 100; // Simple estimation

        var totalDuration = history.Any() && history[^1].CompletedAt.HasValue
            ? history[^1].CompletedAt!.Value - history[0].Timestamp
            : TimeSpan.Zero;

        return new ContextStats
        {
            TurnCount = turnCount,
            EstimatedTokens = estimatedTokens,
            TotalDuration = totalDuration
        };
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
        _interactiveAgent?.Dispose();
    }
}
