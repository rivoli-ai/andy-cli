using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Acp.Core.Agent;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.ACP;

/// <summary>
/// Agent provider that integrates Andy.CLI with ACP protocol for Zed
/// </summary>
public class AndyAgentProvider : IAgentProvider
{
    private readonly ILlmProvider _llmProvider;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly ILogger<AndyAgentProvider>? _logger;
    private readonly ConcurrentDictionary<string, SimpleAgent> _sessions = new();
    private readonly string _systemPrompt;

    public AndyAgentProvider(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ILogger<AndyAgentProvider>? logger = null)
    {
        _llmProvider = llmProvider ?? throw new ArgumentNullException(nameof(llmProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _logger = logger;

        // Build system prompt
        _systemPrompt = Andy.Cli.Services.Prompts.SystemPrompts.GetDefaultCliPrompt();
    }

    public AgentCapabilities GetCapabilities()
    {
        return new AgentCapabilities
        {
            LoadSession = true,
            AudioPrompts = false,
            ImagePrompts = false,
            EmbeddedContext = true
        };
    }

    public Task<SessionMetadata> CreateSessionAsync(NewSessionParams? parameters, CancellationToken cancellationToken)
    {
        var sessionId = $"session-{Guid.NewGuid():N}";

        // Create a new SimpleAgent for this session
        var agent = new SimpleAgent(
            _llmProvider,
            _toolRegistry,
            _toolExecutor,
            _systemPrompt,
            maxTurns: 10,
            logger: _logger as ILogger<SimpleAgent>
        );

        _sessions[sessionId] = agent;

        _logger?.LogInformation("Created new Andy session: {SessionId}", sessionId);

        return Task.FromResult(new SessionMetadata
        {
            SessionId = sessionId,
            CreatedAt = DateTime.UtcNow,
            Mode = "assistant",
            Model = "andy-cli",
            Metadata = new Dictionary<string, object>
            {
                ["provider"] = "andy-cli",
                ["tools_count"] = _toolRegistry.GetTools().Count()
            }
        });
    }

    public Task<SessionMetadata?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var agent))
        {
            _logger?.LogInformation("Loaded existing session: {SessionId}", sessionId);

            return Task.FromResult<SessionMetadata?>(new SessionMetadata
            {
                SessionId = sessionId,
                CreatedAt = DateTime.UtcNow, // We don't track this currently
                LastAccessedAt = DateTime.UtcNow,
                MessageCount = 0, // SimpleAgent doesn't expose this
                Mode = "assistant",
                Model = "andy-cli"
            });
        }

        _logger?.LogWarning("Session not found: {SessionId}", sessionId);
        return Task.FromResult<SessionMetadata?>(null);
    }

    public async Task<AgentResponse> ProcessPromptAsync(
        string sessionId,
        PromptMessage prompt,
        IResponseStreamer streamer,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var agent))
        {
            _logger?.LogError("Session not found for prompt: {SessionId}", sessionId);
            return new AgentResponse
            {
                Message = "Error: Session not found",
                StopReason = StopReason.Error,
                Error = "Session not found"
            };
        }

        try
        {
            _logger?.LogInformation("Processing prompt for session {SessionId}: {Prompt}",
                sessionId, prompt.Text.Substring(0, Math.Min(100, prompt.Text.Length)));

            // Process the message through the agent
            var result = await agent.ProcessMessageAsync(prompt.Text, cancellationToken);

            // Stream the response word by word
            if (result.Success && !string.IsNullOrEmpty(result.Response))
            {
                await StreamResponse(result.Response, streamer, cancellationToken);
            }

            return new AgentResponse
            {
                Message = result.Response ?? "",
                StopReason = result.Success ? StopReason.Completed : StopReason.Error,
                Error = result.Success ? null : result.StopReason
            };
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Prompt processing cancelled for session {SessionId}", sessionId);
            return new AgentResponse
            {
                Message = "",
                StopReason = StopReason.Cancelled
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing prompt for session {SessionId}", sessionId);
            return new AgentResponse
            {
                Message = $"Error: {ex.Message}",
                StopReason = StopReason.Error,
                Error = ex.Message
            };
        }
    }

    private async Task StreamResponse(string response, IResponseStreamer streamer, CancellationToken cancellationToken)
    {
        // Split response into words and stream them
        var words = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            await streamer.SendMessageChunkAsync(word + " ", cancellationToken);

            // Small delay to simulate streaming (optional)
            await Task.Delay(10, cancellationToken);
        }
    }

    public Task<bool> SetSessionModeAsync(string sessionId, string mode, CancellationToken cancellationToken)
    {
        // Mode switching not supported yet
        _logger?.LogInformation("Mode switch requested for session {SessionId}: {Mode} (not implemented)", sessionId, mode);
        return Task.FromResult(false);
    }

    public Task<bool> SetSessionModelAsync(string sessionId, string model, CancellationToken cancellationToken)
    {
        // Model switching not supported yet
        _logger?.LogInformation("Model switch requested for session {SessionId}: {Model} (not implemented)", sessionId, model);
        return Task.FromResult(false);
    }

    public Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Cancel requested for session {SessionId}", sessionId);

        // SimpleAgent doesn't expose cancellation, but we can note it
        return Task.CompletedTask;
    }
}
