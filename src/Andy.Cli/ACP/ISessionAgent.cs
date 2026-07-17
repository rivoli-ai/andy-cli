using System;
using System.Threading;
using System.Threading.Tasks;
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
    Task<SimpleAgentResult> ProcessMessageAsync(string userMessage, CancellationToken cancellationToken);
}

/// <summary>Creates <see cref="ISessionAgent"/> instances for new sessions.</summary>
public interface ISessionAgentFactory
{
    ISessionAgent Create(string systemPrompt);
}

/// <summary>Adapts the engine's <see cref="SimpleAgent"/> to <see cref="ISessionAgent"/>.</summary>
internal sealed class SimpleAgentSessionAgent : ISessionAgent
{
    private readonly SimpleAgent _agent;

    public SimpleAgentSessionAgent(SimpleAgent agent) => _agent = agent;

    public Task<SimpleAgentResult> ProcessMessageAsync(string userMessage, CancellationToken cancellationToken)
        => _agent.ProcessMessageAsync(userMessage, cancellationToken);

    public void Dispose() => _agent.Dispose();
}

/// <summary>
/// Default factory that builds a real engine <see cref="SimpleAgent"/> backed
/// by the configured LLM provider, tool registry, and executor.
/// </summary>
internal sealed class SimpleAgentSessionAgentFactory : ISessionAgentFactory
{
    private readonly ILlmProvider _llmProvider;
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

    public ISessionAgent Create(string systemPrompt)
    {
        var agent = new SimpleAgent(
            _llmProvider,
            _toolRegistry,
            _toolExecutor,
            systemPrompt,
            maxTurns: _maxTurns,
            logger: AndyAgentProvider.CreateAgentLogger(_loggerFactory));

        return new SimpleAgentSessionAgent(agent);
    }
}
