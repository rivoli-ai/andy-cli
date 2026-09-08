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
public class AndyAgentProvider : IAgentProvider, ISessionConfigProvider, ISessionCatalogProvider, IDisposable
{
    private readonly AcpSessionStore? _store;
    private readonly IAcpHistoryReplay? _replay;
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
        IReadOnlyList<AcpModelSelection>? modelSelections = null,
        AcpSessionStore? sessionStore = null,
        IAcpHistoryReplay? historyReplay = null)
    {
        if (llmProvider == null) throw new ArgumentNullException(nameof(llmProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        if (toolExecutor == null) throw new ArgumentNullException(nameof(toolExecutor));
        _logger = logger;
        _store = sessionStore;
        _replay = historyReplay;
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
        // - LoadSession: restores stored engine snapshots and replays history
        //   in the production v1 transport; unknown ids are rejected.
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

        cancellationToken.ThrowIfCancellationRequested();
        if (!Andy.Cli.Services.Sessions.SessionStore.IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid ACP session id.");
        if (_store?.Load(sessionId) != null) throw new InvalidOperationException("Session already exists; use session/load.");
        var cwd = Path.GetFullPath(parameters?.Cwd ?? Environment.CurrentDirectory);
        var mode = string.IsNullOrWhiteSpace(parameters?.Mode) ? "assistant" : parameters!.Mode;
        var model = string.IsNullOrWhiteSpace(parameters?.Model) ? _defaultModel : parameters!.Model;

        // ACP passes the workspace cwd with session/new; surface it to the model
        // through the system prompt so relative references resolve sensibly.
        var systemPrompt = string.IsNullOrWhiteSpace(parameters?.Cwd)
            ? _systemPrompt
            : _systemPrompt + $"\n\nThe user's working directory is: {parameters!.Cwd}";

        // Create a new engine agent for this session.
        var selection = ResolveInitialSelection(model);
        var agent = _agentFactory.Create(systemPrompt, selection.ProviderId, selection.ModelId, cwd);

        var entry = new AcpSessionEntry(
            sessionId,
            agent,
            mode,
            selection.ModelId,
            selection.ProviderId,
            systemPrompt, cwd);
        Persist(entry);
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

    public async Task<SessionMetadata?> LoadSessionAsync(LoadSessionParams parameters,
        IResponseStreamer streamer, CancellationToken cancellationToken)
    {
        var entry = RestoreEntry(parameters, cancellationToken);
        if (entry == null) return null;
        var token = entry.BeginPrompt(cancellationToken);
        try
        {
            if (_replay != null && entry.Agent?.ExportTranscript() is { } snapshot)
                await _replay.ReplayAsync(entry.SessionId, snapshot, streamer, token).ConfigureAwait(false);
            return MetadataFor(entry);
        }
        finally { entry.EndPrompt(); }
    }

    public Task<SessionMetadata?> ResumeSessionAsync(LoadSessionParams parameters, CancellationToken cancellationToken)
    {
        var entry = RestoreEntry(parameters, cancellationToken);
        return Task.FromResult(entry == null ? null : MetadataFor(entry));
    }

    private AcpSessionEntry? RestoreEntry(LoadSessionParams parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_sessions.TryGet(parameters.SessionId, out var entry))
        {
            var record = _store?.Load(parameters.SessionId);
            if (record == null) return null;
            if (!string.IsNullOrEmpty(parameters.Cwd) && Path.GetFullPath(parameters.Cwd) != record.Cwd)
                throw new InvalidOperationException("Session belongs to a different working directory.");
            var prompt = _systemPrompt + $"\n\nThe user's working directory is: {record.Cwd}";
            var agent = _agentFactory.Create(prompt, record.Provider, record.Model, record.Cwd);
            try
            {
                agent.RestoreTranscript(record.Snapshot);
                entry = new AcpSessionEntry(record.SessionId, agent, record.Mode, record.Model, record.Provider,
                    prompt, record.Cwd, record.CreatedAt, record.Snapshot.Turns.Count);
                _sessions.Add(entry);
            }
            catch { agent.Dispose(); throw; }
        }
        if (entry.HasActivePrompt) throw new InvalidOperationException("Session has an active prompt.");
        if (!string.IsNullOrEmpty(parameters.Cwd) && Path.GetFullPath(parameters.Cwd) != entry.Cwd)
            throw new InvalidOperationException("Session belongs to a different working directory.");
        return entry;
    }

    private SessionMetadata MetadataFor(AcpSessionEntry entry) => new()
    {
        SessionId = entry.SessionId,
        CreatedAt = entry.CreatedAt,
        LastAccessedAt = entry.LastAccessedAt,
        MessageCount = entry.MessageCount,
        Mode = entry.Mode,
        Model = entry.Model,
        ConfigOptions = BuildConfigOptions(entry),
        Metadata = new Dictionary<string, object>
        {
            ["provider"] = entry.Provider,
            ["cwd"] = entry.Cwd,
            ["tools_count"] = _toolRegistry.GetTools().Count()
        }
    };

    private void Persist(AcpSessionEntry entry)
    {
        if (_store == null || entry.Agent?.ExportTranscript() is not { } snapshot) return;
        _store.Save(new AcpStoredSession
        {
            SessionId = entry.SessionId,
            Cwd = entry.Cwd,
            Provider = entry.Provider,
            Model = entry.Model,
            Mode = entry.Mode,
            CreatedAt = entry.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            Snapshot = snapshot
        });
    }

    public Task<SessionListResult> ListSessionsAsync(string? cwd, string? cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = (_store?.List() ?? []).Where(r => cwd == null || r.Cwd == Path.GetFullPath(cwd)).ToArray();
        var start = 0;
        if (cursor != null)
        {
            start = Array.FindIndex(records, r => r.SessionId == cursor) + 1;
            if (start == 0) throw new ArgumentException("Invalid or expired session cursor.", nameof(cursor));
        }
        var page = records.Skip(start).Take(50).ToArray();
        return Task.FromResult(new SessionListResult
        {
            Sessions = page.Select(r => new SessionCatalogEntry
            {
                SessionId = r.SessionId,
                Cwd = r.Cwd,
                UpdatedAt = r.UpdatedAt,
                Title = r.Snapshot.Turns.FirstOrDefault()?.User.Content[..Math.Min(100, r.Snapshot.Turns.FirstOrDefault()?.User.Content.Length ?? 0)]
            }).ToList(),
            NextCursor = start + page.Length < records.Length ? page.Last().SessionId : null
        });
    }

    public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions.TryGet(sessionId, out var entry) && entry.HasActivePrompt)
            throw new InvalidOperationException("Close the active session before deleting it.");
        var deleted = _store?.Delete(sessionId) ?? false;
        return Task.FromResult(_sessions.Remove(sessionId) || deleted);
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions.TryGet(sessionId, out var entry))
        {
            await entry.BeginClose().WaitAsync(cancellationToken).ConfigureAwait(false);
            Persist(entry);
            _sessions.Remove(sessionId);
        }
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


            // Thread the linked cancellation token and prompt-specific ACP
            // streamer into the engine call so intermediate narration and real
            // tool execution updates can be displayed by the client.
            var result = await agent.ProcessMessageAsync(effectivePrompt, streamer, linkedToken);

            entry.IncrementMessageCount();
            Persist(entry);

            if (!agent.StreamsResponses && result.Success && !string.IsNullOrEmpty(result.Response))
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
    /// Forwards one complete response for session agents without incremental output.
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
            selection.ModelId, entry.Cwd);

        try
        {
            if (entry.Agent?.ExportTranscript() is { } snapshot) replacement.RestoreTranscript(snapshot);
        }
        catch { replacement.Dispose(); throw; }
        if (!entry.TryReplaceAgent(replacement, selection.ProviderId, selection.ModelId))
        {
            replacement.Dispose();
            throw new InvalidOperationException(
                "The session model cannot be changed while a prompt is running.");
        }

        Persist(entry);
        _logger?.LogInformation(
            "Changed ACP session {SessionId} model to {Provider}/{Model}; conversation context preserved",
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
