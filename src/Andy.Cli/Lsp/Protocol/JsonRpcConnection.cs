using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Lsp.Protocol;

/// <summary>
/// A minimal JSON-RPC 2.0 peer over a pair of streams, sized for the Language Server Protocol.
///
/// Design constraints that shaped this class:
/// - Nothing it does may throw into the agent loop. The read loop swallows malformed frames, and a
///   dead connection fails every pending request rather than leaving them hanging forever.
/// - Every request is cancellable and every request has an owner: cancelling a request removes its
///   pending entry and sends $/cancelRequest, so a cancelled turn does not leak a waiter.
/// - Server-to-client requests are answered (with an empty result or MethodNotFound) instead of
///   ignored; several real servers block on workspace/configuration and window/workDoneProgress
///   before they will publish anything.
/// </summary>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _writeStream;
    private readonly LspFrameReader _reader;
    private readonly ILogger? _logger;
    private readonly string _name;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();

    private int _nextId;
    private int _disposed;
    private Task _readLoop = Task.CompletedTask;

    public JsonRpcConnection(Stream readStream, Stream writeStream, string name, ILogger? logger)
    {
        _writeStream = writeStream;
        _reader = new LspFrameReader(readStream);
        _name = name;
        _logger = logger;
    }

    /// <summary>Raised for every notification the peer sends. Never throws into the read loop.</summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>Completes when the read loop ends (peer closed the stream or the connection was disposed).</summary>
    public Task Completed => _readLoop;

    /// <summary>Why the connection ended, when it ended abnormally.</summary>
    public string? FaultReason { get; private set; }

    /// <summary>Number of malformed frames tolerated so far. Surfaced by /lsp status.</summary>
    public int MalformedMessageCount { get; private set; }

    public void Start() => _readLoop = Task.Run(ReadLoopAsync);

    public async Task<JsonNode?> SendRequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        if (_readLoop.IsCompleted && _disposed == 0 && FaultReason is not null)
        {
            throw new LspConnectionException($"{_name}: {FaultReason}");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null) envelope["params"] = parameters;

        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
                // Best-effort: tell the server to stop working on it. Failure is irrelevant - the
                // caller has already been released.
                _ = SendNotificationAsync("$/cancelRequest", new JsonObject { ["id"] = id }, CancellationToken.None);
            }
        });

        try
        {
            await WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        return await completion.Task.ConfigureAwait(false);
    }

    public async Task SendNotificationAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (parameters is not null) envelope["params"] = parameters;

        try
        {
            await WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A notification is fire-and-forget by definition; a dead pipe is reported by the read
            // loop, which is the single place that decides the connection is gone.
            _logger?.LogDebug(ex, "LSP {Server}: dropped notification {Method}", _name, method);
        }
    }

    private async Task WriteAsync(JsonNode envelope, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LspFrameWriter.WriteAsync(_writeStream, envelope.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                string? frame;
                try
                {
                    frame = await _reader.ReadFrameAsync(OnMalformed, _shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    FaultReason ??= "connection closed";
                    break;
                }

                if (frame is null)
                {
                    FaultReason ??= "server closed its output stream";
                    break;
                }

                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(frame);
                }
                catch (JsonException ex)
                {
                    OnMalformed($"unparsable JSON payload ({ex.Message})");
                    continue;
                }

                if (message is not JsonObject obj)
                {
                    OnMalformed("payload was not a JSON object");
                    continue;
                }

                try
                {
                    await DispatchAsync(obj).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A handler must never be able to kill the loop.
                    _logger?.LogDebug(ex, "LSP {Server}: message dispatch failed", _name);
                }
            }
        }
        catch (Exception ex)
        {
            FaultReason ??= ex.Message;
            _logger?.LogDebug(ex, "LSP {Server}: read loop ended unexpectedly", _name);
        }
        finally
        {
            FailAllPending();
        }
    }

    private async Task DispatchAsync(JsonObject message)
    {
        var hasId = message.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var method = message.TryGetPropertyValue("method", out var methodNode) ? methodNode?.GetValue<string>() : null;

        if (hasId && method is null)
        {
            // A response to one of our requests.
            if (idNode is not JsonValue idValue || !idValue.TryGetValue<int>(out var id))
            {
                OnMalformed("response carried a non-numeric id");
                return;
            }

            if (!_pending.TryRemove(id, out var pending)) return;

            if (message.TryGetPropertyValue("error", out var errorNode) && errorNode is JsonObject error)
            {
                var code = error.TryGetPropertyValue("code", out var codeNode) ? codeNode?.ToJsonString() : "?";
                var text = error.TryGetPropertyValue("message", out var textNode) ? textNode?.GetValue<string>() : null;
                pending.TrySetException(new LspConnectionException($"{_name}: request failed ({code}) {text}"));
                return;
            }

            message.TryGetPropertyValue("result", out var result);
            pending.TrySetResult(result?.DeepClone());
            return;
        }

        if (method is null)
        {
            OnMalformed("message had neither a method nor a response id");
            return;
        }

        if (hasId)
        {
            await RespondToServerRequestAsync(idNode!, method).ConfigureAwait(false);
            return;
        }

        message.TryGetPropertyValue("params", out var parameters);
        NotificationReceived?.Invoke(method, parameters?.DeepClone());
    }

    /// <summary>
    /// Answers a server-initiated request. Andy exposes no client-side capabilities beyond
    /// diagnostics, so the answer is always an inert one; the point is to unblock servers that
    /// wait for a reply before they start analyzing.
    /// </summary>
    private async Task RespondToServerRequestAsync(JsonNode id, string method)
    {
        JsonNode? result = method switch
        {
            "workspace/configuration" => new JsonArray { new JsonObject() },
            "client/registerCapability" or "client/unregisterCapability" => null,
            "window/workDoneProgress/create" => null,
            _ => null,
        };

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        };

        try
        {
            await WriteAsync(response, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The peer went away mid-request; the read loop will notice.
        }
    }

    private void OnMalformed(string reason)
    {
        MalformedMessageCount++;
        _logger?.LogDebug("LSP {Server}: malformed message ignored ({Reason})", _name, reason);
    }

    private void FailAllPending()
    {
        var reason = FaultReason ?? "connection closed";
        foreach (var key in new List<int>(_pending.Keys))
        {
            if (_pending.TryRemove(key, out var pending))
            {
                pending.TrySetException(new LspConnectionException($"{_name}: {reason}"));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        FaultReason ??= "connection disposed";
        _shutdown.Cancel();
        FailAllPending();

        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // The read loop is blocked on a pipe the transport is about to tear down.
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>A language server connection failed, crashed, or answered with an error.</summary>
public sealed class LspConnectionException : Exception
{
    public LspConnectionException(string message) : base(message) { }
    public LspConnectionException(string message, Exception inner) : base(message, inner) { }
}
