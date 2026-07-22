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
    Task<SimpleAgentResult> ProcessMessageAsync(
        string userMessage,
        IResponseStreamer streamer,
        CancellationToken cancellationToken);
}

/// <summary>Creates <see cref="ISessionAgent"/> instances for new sessions.</summary>
public interface ISessionAgentFactory
{
    ISessionAgent Create(string systemPrompt, string provider, string model);
}

/// <summary>Adapts the engine's <see cref="SimpleAgent"/> to <see cref="ISessionAgent"/>.</summary>
internal sealed class SimpleAgentSessionAgent : ISessionAgent
{
    private readonly SimpleAgent _agent;
    private readonly AcpSessionUpdateSink _sink;
    private readonly IDisposable? _owner;

    public SimpleAgentSessionAgent(SimpleAgent agent, AcpSessionUpdateSink sink, IDisposable? owner = null)
    {
        _agent = agent;
        _sink = sink;
        _owner = owner;
    }

    public async Task<SimpleAgentResult> ProcessMessageAsync(
        string userMessage,
        IResponseStreamer streamer,
        CancellationToken cancellationToken)
    {
        _sink.Attach(streamer);
        try
        {
            return await _agent.ProcessMessageAsync(userMessage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sink.Detach(streamer);
        }
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

    public SimpleAgentSessionAgentFactory(
        ILlmProvider llmProvider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ILoggerFactory? loggerFactory,
        int maxTurns = 10)
    {
        _llmProvider = llmProvider;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _loggerFactory = loggerFactory;
        _maxTurns = maxTurns;
    }

    public SimpleAgentSessionAgentFactory(
        Func<string, string, (ILlmProvider Provider, IDisposable? Owner)> providerFactory,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ILoggerFactory? loggerFactory,
        int maxTurns = 10)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _llmProvider = null!;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _loggerFactory = loggerFactory;
        _maxTurns = maxTurns;
    }

    public ISessionAgent Create(string systemPrompt, string provider, string model)
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
            logger: AndyAgentProvider.CreateAgentLogger(_loggerFactory));

        return new SimpleAgentSessionAgent(agent, sink, lease.Item2);
    }
}
