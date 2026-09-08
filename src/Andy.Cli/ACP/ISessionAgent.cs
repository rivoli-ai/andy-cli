using System;
using System.Threading;
using System.Threading.Tasks;
using Andy.Acp.Core.Agent;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.ACP;

/// <summary>
/// Abstraction over the per-session engine agent. Decouples the ACP provider
/// from the concrete <see cref="SimpleAgent"/> so the session lifecycle,
/// cancellation, and streaming behavior can be exercised deterministically in
/// tests with a fake agent.
/// </summary>
public interface ISessionAgent : IDisposable
{
    bool StreamsResponses => false;
    TranscriptSnapshot? ExportTranscript() => null;
    void RestoreTranscript(TranscriptSnapshot snapshot) => throw new NotSupportedException("Session agent does not support restoration.");
    Task<SimpleAgentResult> ProcessMessageAsync(
        string userMessage,
        IResponseStreamer streamer,
        CancellationToken cancellationToken);
}

/// <summary>Creates <see cref="ISessionAgent"/> instances for new sessions.</summary>
public interface ISessionAgentFactory
{
    ISessionAgent Create(string systemPrompt, string provider, string model);
    ISessionAgent Create(string systemPrompt, string provider, string model, string cwd) => Create(systemPrompt, provider, model);
}

/// <summary>Adapts the engine's <see cref="SimpleAgent"/> to <see cref="ISessionAgent"/>.</summary>
internal sealed class SimpleAgentSessionAgent : ISessionAgent
{
    private readonly SimpleAgent _agent;
    private readonly AcpSessionUpdateSink _sink;
    private readonly IDisposable? _owner;
    private readonly string? _nameOverride;

    public SimpleAgentSessionAgent(SimpleAgent agent, AcpSessionUpdateSink sink, IDisposable? owner = null, string? nameOverride = null)
    {
        _agent = agent;
        _sink = sink;
        _owner = owner;
        _nameOverride = nameOverride;
    }

    public bool StreamsResponses => true;

    public async Task<SimpleAgentResult> ProcessMessageAsync(
        string userMessage,
        IResponseStreamer streamer,
        CancellationToken cancellationToken)
    {
        _sink.Attach(streamer);
        try
        {
            return await _agent.ProcessMessageAsync(userMessage, delta =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (delta.Kind == AgentResponseDeltaKind.Text && !string.IsNullOrEmpty(delta.Text))
                    _sink.SendMessageAsync(delta.Text, cancellationToken).GetAwaiter().GetResult();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sink.Detach(streamer);
        }
    }

    public TranscriptSnapshot ExportTranscript() => _agent.ExportTranscript();
    public void RestoreTranscript(TranscriptSnapshot snapshot)
    {
        _agent.RestoreTranscript(snapshot);
        if (_nameOverride is not null) _agent.Identity.SetName(_nameOverride);
    }

    public void Dispose()
    {
        _agent.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>
/// Default factory that builds a real engine <see cref="SimpleAgent"/> backed
/// by the configured LLM provider, tool registry, and executor.
/// </summary>
internal sealed class SimpleAgentSessionAgentFactory : ISessionAgentFactory
{
    private readonly ILlmProvider _llmProvider;
    private readonly Func<string, string, (ILlmProvider Provider, IDisposable? Owner)>? _providerFactory;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly int _maxTurns;
    private readonly string? _agentName;

    public SimpleAgentSessionAgentFactory(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ILoggerFactory? loggerFactory,
        int maxTurns = 10, string? agentName = null)
    {
        _llmProvider = llmProvider;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _loggerFactory = loggerFactory;
        _maxTurns = maxTurns;
        _agentName = agentName;
    }

    public SimpleAgentSessionAgentFactory(
        Func<string, string, (ILlmProvider Provider, IDisposable? Owner)> providerFactory,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ILoggerFactory? loggerFactory,
        int maxTurns = 10, string? agentName = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _llmProvider = null!;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _loggerFactory = loggerFactory;
        _maxTurns = maxTurns;
        _agentName = agentName;
    }

    public ISessionAgent Create(string systemPrompt, string provider, string model)
        => Create(systemPrompt, provider, model, Environment.CurrentDirectory);

    public ISessionAgent Create(string systemPrompt, string provider, string model, string cwd)
    {
        var lease = _providerFactory?.Invoke(provider, model) ?? (_llmProvider, null);
        var sink = new AcpSessionUpdateSink(_loggerFactory?.CreateLogger<AcpSessionUpdateSink>());
        var progressProvider = new AcpProgressLlmProvider(lease.Item1, sink);
        var observingExecutor = new AcpObservingToolExecutor(_toolExecutor, sink);
        var agent = new SimpleAgent(
            progressProvider,
            _toolRegistry,
            observingExecutor,
            systemPrompt,
            maxTurns: _maxTurns,
            workingDirectory: cwd,
            logger: AndyAgentProvider.CreateAgentLogger(_loggerFactory));

        if (_agentName is not null) agent.Identity.SetName(_agentName);
        return new SimpleAgentSessionAgent(agent, sink, lease.Item2, _agentName);
    }
}
