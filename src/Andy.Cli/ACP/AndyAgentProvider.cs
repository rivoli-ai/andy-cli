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
public class AndyAgentProvider : IAgentProvider, ISessionConfigProvider, IDisposable
{
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<AndyAgentProvider>? _logger;
    private readonly ISessionAgentFactory _agentFactory;
    private readonly AndySessionRegistry _sessions;
    private readonly string _systemPrompt;
    private readonly string _defaultModel;
    private readonly string _defaultProvider;
    private readonly IReadOnlyList<AcpModelSelection> _modelSelections;
    private readonly Dictionary<string, AcpModelSelection> _modelSelectionsById;

    public AndyAgentProvider(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ILogger<AndyAgentProvider>? logger = null,
        ILoggerFactory? loggerFactory = null,
        int maxSessions = AndySessionRegistry.DefaultMaxSessions,
        string? defaultModel = null,
        ISessionAgentFactory? agentFactory = null,
        string? defaultProvider = null,
        IReadOnlyList<AcpModelSelection>? modelSelections = null)
    {
        if (llmProvider == null) throw new ArgumentNullException(nameof(llmProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        if (toolExecutor == null) throw new ArgumentNullException(nameof(toolExecutor));
        _logger = logger;
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel) ? "andy-cli" : defaultModel!;
        _defaultProvider = string.IsNullOrWhiteSpace(defaultProvider) ? "andy-cli" : defaultProvider!;
        _agentFactory = agentFactory
            ?? new SimpleAgentSessionAgentFactory(llmProvider, toolRegistry, toolExecutor, loggerFactory);
        _sessions = new AndySessionRegistry(maxSessions, logger);
        _modelSelections = modelSelections ?? AcpModelCatalog.Build(
            options: null,
            _defaultProvider,
            _defaultModel);
        if (_modelSelections.Count == 0)
        {
            throw new ArgumentException("At least one ACP model selection is required.", nameof(modelSelections));
        }

        _modelSelectionsById = _modelSelections.ToDictionary(
            selection => selection.ValueId,
            StringComparer.Ordinal);

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
        var selection = ResolveInitialSelection(model);
        var agent = _agentFactory.Create(systemPrompt, selection.ProviderId, selection.ModelId);

        var entry = new AcpSessionEntry(
            sessionId,
            agent,
            mode,
            selection.ModelId,
            selection.ProviderId,
            systemPrompt);
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
            Model = selection.ModelId,
            MessageCount = entry.MessageCount,
            ConfigOptions = BuildConfigOptions(entry),
            Metadata = new Dictionary<string, object>
            {
                ["provider"] = selection.ProviderId,
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
                Model = entry.Model,
                ConfigOptions = BuildConfigOptions(entry),
                Metadata = new Dictionary<string, object>
                {
                    ["provider"] = entry.Provider,
                    ["tools_count"] = _toolRegistry.GetTools().Count()
                }
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
        if (!_sessions.TryGet(sessionId, out var entry))
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

            var agent = entry.Agent;
            if (agent == null)
            {
                return new AgentResponse
                {
                    Message = "Error: Session agent not available",
                    StopReason = StopReason.Error,
                    Error = "Session agent not available"
                };
            }

            var effectivePrompt = BuildPrompt(prompt);
            _logger?.LogInformation("Processing prompt for session {SessionId}: {Preview}",
                sessionId, effectivePrompt.Substring(0, Math.Min(100, effectivePrompt.Length)));

            if (entry.TryMarkModelAnnounced())
            {
                await streamer.SendThinkingAsync(
                    $"Model: {entry.Provider}/{entry.Model}", linkedToken);
            }

            await streamer.SendThinkingAsync("Analyzing request...", linkedToken);

            // Thread the linked cancellation token and prompt-specific ACP
            // streamer into the engine call so intermediate narration and real
            // tool execution updates can be displayed by the client.
            var result = await agent.ProcessMessageAsync(effectivePrompt, streamer, linkedToken);

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
        _logger?.LogInformation(
            "Rejecting unsupported mode switch for session {SessionId}: {ModeId}", sessionId, modeId);
        return Task.FromResult(false);
    }

    /// <summary>
    /// Applies the ACP model config option by rebuilding the session's engine
    /// agent with the selected provider/model. Reconfiguration deliberately
    /// resets that session's conversation context; other sessions are unchanged.
    /// </summary>
    public Task<IReadOnlyList<SessionConfigOption>> SetConfigOptionAsync(
        string sessionId,
        string configId,
        SessionConfigValue value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!configId.Equals(AcpModelCatalog.ConfigId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown session config option '{configId}'.", nameof(configId));
        }

        if (!_sessions.TryGet(sessionId, out var entry))
        {
            throw new ArgumentException($"Unknown ACP session '{sessionId}'.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(value?.ValueId) ||
            !_modelSelectionsById.TryGetValue(value.ValueId, out var selection))
        {
            throw new ArgumentException("The selected model is not available.", nameof(value));
        }

        if (entry.Provider.Equals(selection.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            entry.Model.Equals(selection.ModelId, StringComparison.Ordinal))
        {
            return Task.FromResult<IReadOnlyList<SessionConfigOption>>(BuildConfigOptions(entry));
        }

        var replacement = _agentFactory.Create(
            entry.SystemPrompt,
            selection.ProviderId,
            selection.ModelId);

        if (!entry.TryReplaceAgent(replacement, selection.ProviderId, selection.ModelId))
        {
            replacement.Dispose();
            throw new InvalidOperationException(
                "The session model cannot be changed while a prompt is running.");
        }

        _logger?.LogInformation(
            "Changed ACP session {SessionId} model to {Provider}/{Model}; conversation context reset",
            sessionId,
            selection.ProviderId,
            selection.ModelId);

        return Task.FromResult<IReadOnlyList<SessionConfigOption>>(BuildConfigOptions(entry));
    }

    private AcpModelSelection ResolveInitialSelection(string requestedModel)
    {
        return _modelSelections.FirstOrDefault(selection =>
                   selection.ModelId.Equals(requestedModel, StringComparison.Ordinal))
               ?? _modelSelections.FirstOrDefault(selection =>
                   selection.ProviderId.Equals(_defaultProvider, StringComparison.OrdinalIgnoreCase) &&
                   selection.ModelId.Equals(_defaultModel, StringComparison.Ordinal))
               ?? _modelSelections[0];
    }

    private List<SessionConfigOption> BuildConfigOptions(AcpSessionEntry entry)
    {
        var current = _modelSelections.FirstOrDefault(selection =>
            selection.ProviderId.Equals(entry.Provider, StringComparison.OrdinalIgnoreCase) &&
            selection.ModelId.Equals(entry.Model, StringComparison.Ordinal));

        return new List<SessionConfigOption>
        {
            new()
            {
                Id = AcpModelCatalog.ConfigId,
                Name = "Model",
                Description = "Provider and model used for this session",
                Category = "model",
                CurrentValueId = current?.ValueId,
                Groups = _modelSelections
                    .GroupBy(selection => new { selection.ProviderId, selection.ProviderName })
                    .Select(group => new SessionConfigSelectGroup
                    {
                        Group = group.Key.ProviderId,
                        Name = group.Key.ProviderName,
                        Options = group.Select(selection => new SessionConfigSelectOption
                        {
                            Value = selection.ValueId,
                            Name = selection.ModelId,
                            Description = $"Use {selection.ModelId} through {selection.ProviderName}"
                        }).ToList()
                    }).ToList()
            }
        };
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
