using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Acp.Core.Agent;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.ACP;

/// <summary>
/// Agent provider that integrates Andy.CLI with the ACP protocol for Zed.
/// Owns per-session engine agents with an explicit create/load/cancel/dispose
/// lifecycle, bounded retention, and cancellation that reaches the running
/// engine operation.
/// </summary>
public class AndyAgentProvider : IAgentProvider, IDisposable
{
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<AndyAgentProvider>? _logger;
    private readonly ISessionAgentFactory _agentFactory;
    private readonly AndySessionRegistry _sessions;
    private readonly string _systemPrompt;
    private readonly string _defaultModel;

    public AndyAgentProvider(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ILogger<AndyAgentProvider>? logger = null,
        ILoggerFactory? loggerFactory = null,
        int maxSessions = AndySessionRegistry.DefaultMaxSessions,
        string? defaultModel = null,
        ISessionAgentFactory? agentFactory = null)
    {
        if (llmProvider == null) throw new ArgumentNullException(nameof(llmProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        if (toolExecutor == null) throw new ArgumentNullException(nameof(toolExecutor));
        _logger = logger;
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel) ? "andy-cli" : defaultModel!;
        _agentFactory = agentFactory
            ?? new SimpleAgentSessionAgentFactory(llmProvider, toolRegistry, toolExecutor, loggerFactory);
        _sessions = new AndySessionRegistry(maxSessions, logger);

        // Build system prompt
        _systemPrompt = Andy.Cli.Services.Prompts.SystemPrompts.GetDefaultCliPrompt();
    }

    /// <summary>
    /// Creates a typed logger for the engine agent using the injected logger
    /// factory. Returns null when no factory is available. Fixes the previous
    /// bug where an <c>as ILogger&lt;SimpleAgent&gt;</c> cast of an
    /// <c>ILogger&lt;AndyAgentProvider&gt;</c> always produced null.
    /// </summary>
    public static ILogger<SimpleAgent>? CreateAgentLogger(ILoggerFactory? loggerFactory)
        => loggerFactory?.CreateLogger<SimpleAgent>();

    public AgentCapabilities GetCapabilities()
    {
        // Advertise only operations that are actually implemented.
        // - LoadSession: supported for sessions still retained in-memory;
        //   unknown ids are rejected (see LoadSessionAsync).
        // - EmbeddedContext: honored by folding context items into the prompt.
        // - Audio/Image prompts: not implemented.
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
        var sessionId = string.IsNullOrWhiteSpace(parameters?.SessionId)
            ? $"session-{Guid.NewGuid():N}"
            : parameters!.SessionId;

        var mode = string.IsNullOrWhiteSpace(parameters?.Mode) ? "assistant" : parameters!.Mode;
        var model = string.IsNullOrWhiteSpace(parameters?.Model) ? _defaultModel : parameters!.Model;

        // ACP passes the workspace cwd with session/new; surface it to the model
        // through the system prompt so relative references resolve sensibly.
        var systemPrompt = string.IsNullOrWhiteSpace(parameters?.Cwd)
            ? _systemPrompt
            : _systemPrompt + $"\n\nThe user's working directory is: {parameters!.Cwd}";

        // Create a new engine agent for this session.
        var agent = _agentFactory.Create(systemPrompt);

        var entry = new AcpSessionEntry(sessionId, agent, mode, model);
        _sessions.Add(entry);

        _logger?.LogInformation(
            "Created ACP session {SessionId} (retained {Count}/{Max})",
            sessionId, _sessions.Count, _sessions.MaxSessions);

        return Task.FromResult(new SessionMetadata
        {
            SessionId = sessionId,
            CreatedAt = entry.CreatedAt,
            LastAccessedAt = entry.LastAccessedAt,
            Mode = mode,
            Model = model,
            MessageCount = entry.MessageCount,
            Metadata = new Dictionary<string, object>
            {
                ["provider"] = "andy-cli",
                ["tools_count"] = _toolRegistry.GetTools().Count()
            }
        });
    }

    public Task<SessionMetadata?> LoadSessionAsync(
        LoadSessionParams parameters,
        IResponseStreamer streamer,
        CancellationToken cancellationToken)
    {
        var sessionId = parameters.SessionId;

        // Session state lives in-memory only; a session that is no longer
        // retained (evicted, disposed, or from a previous process) cannot be
        // resumed. Reject unknown ids rather than fabricating an empty session.
        // History replay: the conversation lives inside the engine agent, which
        // exposes no transcript API, so no session/update replay is emitted here;
        // the client resumes with metadata only.
        if (_sessions.TryGet(sessionId, out var entry))
        {
            _logger?.LogInformation("Loaded existing ACP session: {SessionId}", sessionId);

            return Task.FromResult<SessionMetadata?>(new SessionMetadata
            {
                SessionId = sessionId,
                CreatedAt = entry.CreatedAt,
                LastAccessedAt = entry.LastAccessedAt,
                MessageCount = entry.MessageCount,
                Mode = entry.Mode,
                Model = entry.Model
            });
        }

        _logger?.LogWarning("Cannot load unknown ACP session: {SessionId}", sessionId);
        return Task.FromResult<SessionMetadata?>(null);
    }

    public async Task<AgentResponse> ProcessPromptAsync(
        string sessionId,
        PromptMessage prompt,
        IResponseStreamer streamer,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGet(sessionId, out var entry) || entry.Agent == null)
        {
            _logger?.LogError("Session not found for prompt: {SessionId}", sessionId);
            return new AgentResponse
            {
                Message = "Error: Session not found",
                StopReason = StopReason.Error,
                Error = "Session not found"
            };
        }

        // BeginPrompt is called INSIDE the try so a session that was
        // concurrently disposed/evicted (ObjectDisposedException) or already
        // busy with another prompt (InvalidOperationException) is turned into a
        // clean protocol error instead of an unhandled exception. EndPrompt runs
        // in the finally only when BeginPrompt actually succeeded, so a rejected
        // second prompt never tears down the first prompt's cancellation source.
        CancellationToken linkedToken = default;
        var promptStarted = false;
        try
        {
            try
            {
                // Link the transport token with the session's cancel source so an
                // explicit ACP cancel request reaches the running engine operation.
                linkedToken = entry.BeginPrompt(cancellationToken);
                promptStarted = true;
            }
            catch (ObjectDisposedException)
            {
                _logger?.LogWarning(
                    "Session {SessionId} was disposed before the prompt could start", sessionId);
                return new AgentResponse
                {
                    Message = "Error: Session no longer available",
                    StopReason = StopReason.Error,
                    Error = "Session no longer available"
                };
            }
            catch (InvalidOperationException ex)
            {
                _logger?.LogWarning(
                    "Rejected concurrent prompt for session {SessionId}: {Reason}", sessionId, ex.Message);
                return new AgentResponse
                {
                    Message = $"Error: {ex.Message}",
                    StopReason = StopReason.Error,
                    Error = ex.Message
                };
            }

            var effectivePrompt = BuildPrompt(prompt);
            _logger?.LogInformation("Processing prompt for session {SessionId}: {Preview}",
                sessionId, effectivePrompt.Substring(0, Math.Min(100, effectivePrompt.Length)));

            // Thread the linked cancellation token into the engine call.
            var result = await entry.Agent.ProcessMessageAsync(effectivePrompt, linkedToken);

            entry.IncrementMessageCount();

            if (result.Success && !string.IsNullOrEmpty(result.Response))
            {
                await StreamResponse(result.Response, streamer, linkedToken);
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
        finally
        {
            if (promptStarted)
            {
                entry.EndPrompt();
            }
        }
    }

    /// <summary>
    /// Folds non-text content blocks into the prompt text (honoring the
    /// advertised EmbeddedContext capability): embedded resources contribute
    /// their text contents, resource links contribute a reference line. Image
    /// and audio blocks are not advertised and never reach this provider.
    /// </summary>
    private static string BuildPrompt(PromptMessage prompt)
    {
        var text = prompt.Text ?? string.Empty;
        if (prompt.Blocks == null || prompt.Blocks.All(b => b.Type == "text"))
        {
            return text;
        }

        var sb = new StringBuilder();
        foreach (var block in prompt.Blocks)
        {
            switch (block.Type)
            {
                case "resource" when !string.IsNullOrWhiteSpace(block.Resource?.Text):
                    var label = string.IsNullOrWhiteSpace(block.Resource!.Uri) ? "resource" : block.Resource.Uri;
                    sb.Append('[').Append(label).Append("]\n").Append(block.Resource.Text).Append("\n\n");
                    break;
                case "resource_link" when !string.IsNullOrWhiteSpace(block.Uri):
                    sb.Append("[linked resource] ").Append(block.Uri).Append("\n\n");
                    break;
            }
        }

        sb.Append(text);
        return sb.ToString();
    }

    /// <summary>
    /// Forwards the model response to the client as a single ordered block.
    /// The engine's <see cref="SimpleAgent.ProcessMessageAsync"/> returns a
    /// complete response and exposes no incremental chunk/token API, so real
    /// token-level streaming is not possible without an engine capability.
    /// The previous artificial word-splitting with Task.Delay (which faked
    /// token cadence over an already-complete string) has been removed.
    /// </summary>
    private static async Task StreamResponse(string response, IResponseStreamer streamer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await streamer.SendMessageChunkAsync(response, cancellationToken);
    }

    public Task<bool> SetSessionModeAsync(string sessionId, string modeId, CancellationToken cancellationToken)
    {
        // Mode switching is not supported by the engine agent. Explicitly
        // decline (returning false) instead of silently accepting the request.
        // (Model selection is likewise unsupported; under conformant ACP it
        // would be exposed via session config options, which this provider does
        // not implement.)
        _logger?.LogInformation(
            "Rejecting unsupported mode switch for session {SessionId}: {ModeId}", sessionId, modeId);
        return Task.FromResult(false);
    }

    public Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGet(sessionId, out var entry))
        {
            var cancelled = entry.CancelActivePrompt();
            _logger?.LogInformation(
                "Cancel requested for session {SessionId} (active operation cancelled: {Cancelled})",
                sessionId, cancelled);
        }
        else
        {
            _logger?.LogWarning("Cancel requested for unknown session {SessionId}", sessionId);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _sessions.Dispose();
        GC.SuppressFinalize(this);
    }
}
