using System.Runtime.CompilerServices;
using System.Text.Json;
using Andy.Acp.Core.Agent;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;
using AcpToolCall = Andy.Acp.Core.Agent.ToolCall;
using AcpToolResult = Andy.Acp.Core.Agent.ToolResult;

namespace Andy.Cli.ACP;

/// <summary>
/// Holds the response streamer for the single prompt currently running in an ACP
/// session. The engine can execute several tools concurrently, so writes are
/// serialized before they reach the JSON-RPC transport.
/// </summary>
internal sealed class AcpSessionUpdateSink
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ILogger? _logger;
    private IResponseStreamer? _streamer;

    public AcpSessionUpdateSink(ILogger? logger = null) => _logger = logger;

    public void Attach(IResponseStreamer streamer)
    {
        ArgumentNullException.ThrowIfNull(streamer);
        lock (_gate)
        {
            if (_streamer != null)
            {
                throw new InvalidOperationException("An ACP response streamer is already attached.");
            }

            _streamer = streamer;
        }
    }

    public void Detach(IResponseStreamer streamer)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_streamer, streamer))
            {
                _streamer = null;
            }
        }
    }

    public Task SendThinkingAsync(string text, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(text)
            ? Task.CompletedTask
            : SendAsync((streamer, token) => streamer.SendThinkingAsync(text, token), cancellationToken);

    public Task SendToolCallAsync(AcpToolCall toolCall, CancellationToken cancellationToken)
        => SendAsync((streamer, token) => streamer.SendToolCallAsync(toolCall, token), cancellationToken);

    public Task SendToolResultAsync(AcpToolResult toolResult, CancellationToken cancellationToken)
        => SendAsync((streamer, token) => streamer.SendToolResultAsync(toolResult, token), cancellationToken);

    private async Task SendAsync(
        Func<IResponseStreamer, CancellationToken, Task> send,
        CancellationToken cancellationToken)
    {
        IResponseStreamer? streamer;
        lock (_gate)
        {
            streamer = _streamer;
        }

        if (streamer == null)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await send(streamer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Progress rendering must not turn a successful model response or
            // tool execution into an engine failure. Transport cancellation is
            // still propagated above and stops the prompt normally.
            _logger?.LogWarning(ex, "Failed to send ACP progress update");
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

/// <summary>
/// Surfaces assistant narration produced on model rounds that also request
/// tools. Those messages are intermediate progress; the no-tool response is the
/// final answer and is sent by <see cref="AndyAgentProvider"/> to avoid duplication.
/// </summary>
internal sealed class AcpProgressLlmProvider : ILlmProvider
{
    private readonly ILlmProvider _inner;
    private readonly AcpSessionUpdateSink _sink;

    public AcpProgressLlmProvider(ILlmProvider inner, AcpSessionUpdateSink sink)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public string Name => _inner.Name;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => _inner.IsAvailableAsync(cancellationToken);

    public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => _inner.ListModelsAsync(cancellationToken);

    public async Task<LlmResponse> CompleteAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.HasToolCalls)
        {
            var narration = string.IsNullOrWhiteSpace(response.Content)
                ? $"Preparing {response.ToolCalls.Count} tool call(s)..."
                : response.Content;
            await _sink.SendThinkingAsync(narration, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _inner.StreamCompleteAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }
}

/// <summary>
/// Observes the exact tool execution boundary owned by the CLI and emits real
/// ACP start/completion updates. This reports the actual result rather than the
/// engine's pre-execution <c>ToolCalled</c> event.
/// </summary>
internal sealed class AcpObservingToolExecutor : IToolExecutor
{
    private const int MaxResultChars = 8_000;

    private readonly IToolExecutor _inner;
    private readonly AcpSessionUpdateSink _sink;

    public AcpObservingToolExecutor(IToolExecutor inner, AcpSessionUpdateSink sink)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted
    {
        add => _inner.ExecutionStarted += value;
        remove => _inner.ExecutionStarted -= value;
    }

    public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted
    {
        add => _inner.ExecutionCompleted += value;
        remove => _inner.ExecutionCompleted -= value;
    }

    public event EventHandler<SecurityViolationEventArgs>? SecurityViolation
    {
        add => _inner.SecurityViolation += value;
        remove => _inner.SecurityViolation -= value;
    }

    public Task<ToolExecutionResult> ExecuteAsync(
        string toolId,
        Dictionary<string, object?> parameters,
        ToolExecutionContext? context = null)
        => ObserveAsync(
            toolId,
            parameters,
            context?.CancellationToken ?? CancellationToken.None,
            () => _inner.ExecuteAsync(toolId, parameters, context));

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ObserveAsync(
            request.ToolId,
            request.Parameters,
            request.Context?.CancellationToken ?? CancellationToken.None,
            () => _inner.ExecuteAsync(request));
    }

    public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
        => _inner.ValidateExecutionRequestAsync(request);

    public Task<ToolResourceUsage?> EstimateResourceUsageAsync(
        string toolId,
        Dictionary<string, object?> parameters)
        => _inner.EstimateResourceUsageAsync(toolId, parameters);

    public Task<int> CancelExecutionsAsync(string correlationId)
        => _inner.CancelExecutionsAsync(correlationId);

    public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions()
        => _inner.GetRunningExecutions();

    public ToolExecutionStatistics GetStatistics() => _inner.GetStatistics();

    private async Task<ToolExecutionResult> ObserveAsync(
        string toolId,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken,
        Func<Task<ToolExecutionResult>> execute)
    {
        var callId = $"tool-{Guid.NewGuid():N}";
        await _sink.SendToolCallAsync(
            new AcpToolCall
            {
                Id = callId,
                Name = toolId,
                Title = Humanize(toolId),
                Kind = InferKind(toolId),
                Status = "in_progress",
                Input = parameters,
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await execute().ConfigureAwait(false);
            await _sink.SendToolResultAsync(
                new AcpToolResult
                {
                    CallId = callId,
                    IsError = !result.IsSuccessful,
                    Content = FormatResult(result),
                },
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _sink.SendToolResultAsync(
                new AcpToolResult
                {
                    CallId = callId,
                    IsError = true,
                    Content = ex.Message,
                },
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static string FormatResult(ToolExecutionResult result)
    {
        string text;
        if (!result.IsSuccessful)
        {
            text = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Tool execution failed."
                : result.ErrorMessage;
        }
        else if (result.Data is string stringData)
        {
            text = stringData;
        }
        else if (result.Data != null)
        {
            try
            {
                text = JsonSerializer.Serialize(result.Data);
            }
            catch
            {
                text = result.Data.ToString() ?? "Tool completed successfully.";
            }
        }
        else
        {
            text = "Tool completed successfully.";
        }

        if (result.IsSuccessful &&
            !string.IsNullOrWhiteSpace(result.Message) &&
            !string.Equals(result.Message, text, StringComparison.Ordinal))
        {
            text = $"{result.Message}\n{text}";
        }

        return text.Length <= MaxResultChars
            ? text
            : text[..MaxResultChars] + "\n... output truncated for display ...";
    }

    private static string Humanize(string toolId)
        => string.Join(' ', toolId.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static string InferKind(string toolId)
    {
        var id = toolId.ToLowerInvariant();
        if (id.Contains("delete") || id.Contains("remove")) return "delete";
        if (id.Contains("move") || id.Contains("rename") || id.Contains("copy")) return "move";
        if (id.Contains("write") || id.Contains("edit") || id.Contains("patch") || id.Contains("create")) return "edit";
        if (id.Contains("search") || id.Contains("find") || id.Contains("grep")) return "search";
        if (id.Contains("execute") || id.Contains("command") || id.Contains("terminal")) return "execute";
        if (id.Contains("fetch") || id.Contains("http") || id.Contains("web")) return "fetch";
        if (id.Contains("read") || id.Contains("list") || id.Contains("info")) return "read";
        return "other";
    }
}
