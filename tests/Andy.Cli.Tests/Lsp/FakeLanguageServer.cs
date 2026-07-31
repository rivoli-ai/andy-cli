using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Lsp;
using Andy.Cli.Lsp.Protocol;

namespace Andy.Cli.Tests.Lsp;

/// <summary>How the deterministic test server misbehaves, if at all.</summary>
public enum FakeServerBehavior
{
    /// <summary>Answers initialize and publishes diagnostics derived from the document text.</summary>
    Normal,

    /// <summary>Accepts the connection but never answers initialize (a server that hangs on startup).</summary>
    NeverInitialize,

    /// <summary>Answers initialize, then closes its output stream on the first document sync (a crash).</summary>
    CrashOnFirstSync,

    /// <summary>Emits unframed junk and a malformed frame before every valid publish.</summary>
    GarbageBeforePublish,

    /// <summary>Answers initialize but never publishes diagnostics (an analyzer that never finishes).</summary>
    NeverPublish,
}

/// <summary>
/// A deterministic in-repo language server.
///
/// It speaks the real base protocol (via the product's own <see cref="LspFrameReader"/> /
/// <see cref="LspFrameWriter"/>) over real streams, so the client's framing, JSON-RPC dispatch,
/// document synchronization and diagnostics parsing are all exercised for real. Nothing about the
/// tests depends on a language server being installed.
///
/// Its diagnostics are a pure function of the document text, which is what makes "diagnostics
/// describe the file as it exists on disk" testable:
///   a line containing ERROR  -> one error   diagnostic on that line
///   a line containing WARN   -> one warning diagnostic on that line
///   a line containing FLOOD  -> 60 errors, for exercising the per-file bounds
/// </summary>
public sealed class FakeLanguageServer
{
    private readonly LoopbackStream _clientToServer;
    private readonly LoopbackStream _serverToClient;
    private readonly FakeServerBehavior _behavior;
    private readonly TimeSpan _publishDelay;
    private readonly CancellationTokenSource _stop = new();

    private int _syncCount;

    public FakeLanguageServer(
        LoopbackStream clientToServer,
        LoopbackStream serverToClient,
        FakeServerBehavior behavior = FakeServerBehavior.Normal,
        TimeSpan? publishDelay = null)
    {
        _clientToServer = clientToServer;
        _serverToClient = serverToClient;
        _behavior = behavior;
        _publishDelay = publishDelay ?? TimeSpan.Zero;
    }

    /// <summary>Number of initialize requests answered. Used to prove a server starts exactly once.</summary>
    public int InitializeCount;

    public int DidOpenCount;
    public int DidChangeCount;
    public int DidSaveCount;
    public int ShutdownCount;

    /// <summary>Every document text the server was given, newest last.</summary>
    public ConcurrentQueue<string> ReceivedTexts { get; } = new();

    public Task Run() => Task.Run(LoopAsync);

    public void Stop()
    {
        _stop.Cancel();
        _serverToClient.CompleteWriting();
    }

    private async Task LoopAsync()
    {
        var reader = new LspFrameReader(_clientToServer);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frame = await reader.ReadFrameAsync(null, _stop.Token).ConfigureAwait(false);
                if (frame is null) break;

                if (JsonNode.Parse(frame) is not JsonObject message) continue;
                await HandleAsync(message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the test.
        }
        catch (Exception)
        {
            // A fake server that falls over is still a valid scenario for the client under test.
        }
        finally
        {
            _serverToClient.CompleteWriting();
        }
    }

    private async Task HandleAsync(JsonObject message)
    {
        var method = message.TryGetPropertyValue("method", out var methodNode)
            ? methodNode?.GetValue<string>()
            : null;
        var id = message.TryGetPropertyValue("id", out var idNode) ? idNode : null;

        switch (method)
        {
            case "initialize":
                Interlocked.Increment(ref InitializeCount);
                if (_behavior == FakeServerBehavior.NeverInitialize) return;
                await RespondAsync(id, new JsonObject
                {
                    ["capabilities"] = new JsonObject { ["textDocumentSync"] = 1 },
                }).ConfigureAwait(false);
                return;

            case "initialized":
                return;

            case "shutdown":
                Interlocked.Increment(ref ShutdownCount);
                await RespondAsync(id, null).ConfigureAwait(false);
                return;

            case "exit":
                Stop();
                return;

            case "textDocument/didOpen":
                Interlocked.Increment(ref DidOpenCount);
                await OnSyncAsync(message, "textDocument").ConfigureAwait(false);
                return;

            case "textDocument/didChange":
                Interlocked.Increment(ref DidChangeCount);
                await OnChangeAsync(message).ConfigureAwait(false);
                return;

            case "textDocument/didSave":
                Interlocked.Increment(ref DidSaveCount);
                return;

            default:
                if (id is not null) await RespondAsync(id, null).ConfigureAwait(false);
                return;
        }
    }

    private async Task OnSyncAsync(JsonObject message, string containerName)
    {
        var parameters = message["params"] as JsonObject;
        var document = parameters?[containerName] as JsonObject;
        var uri = document?["uri"]?.GetValue<string>();
        var text = document?["text"]?.GetValue<string>() ?? string.Empty;
        await PublishAsync(uri, text).ConfigureAwait(false);
    }

    private async Task OnChangeAsync(JsonObject message)
    {
        var parameters = message["params"] as JsonObject;
        var uri = (parameters?["textDocument"] as JsonObject)?["uri"]?.GetValue<string>();
        var changes = parameters?["contentChanges"] as JsonArray;
        var text = (changes is { Count: > 0 } ? changes[0] as JsonObject : null)?["text"]?.GetValue<string>()
            ?? string.Empty;
        await PublishAsync(uri, text).ConfigureAwait(false);
    }

    private async Task PublishAsync(string? uri, string text)
    {
        ReceivedTexts.Enqueue(text);

        if (Interlocked.Increment(ref _syncCount) == 1 && _behavior == FakeServerBehavior.CrashOnFirstSync)
        {
            // Simulate a server that dies mid-analysis: the pipe simply ends.
            Stop();
            return;
        }

        if (_behavior == FakeServerBehavior.NeverPublish || uri is null) return;

        if (_publishDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(_publishDelay, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (_behavior == FakeServerBehavior.GarbageBeforePublish)
        {
            await WriteRawAsync("this is not a frame at all\r\n\r\n").ConfigureAwait(false);
            await WriteRawAsync("Content-Length: 7\r\n\r\n{oops!}").ConfigureAwait(false);
        }

        var diagnostics = new JsonArray();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains("FLOOD", StringComparison.Ordinal))
            {
                for (var extra = 0; extra < 60; extra++)
                {
                    diagnostics.Add(Diagnostic(index, extra, 1, $"flooded diagnostic {extra}", "FLOOD"));
                }
                continue;
            }

            if (lines[index].Contains("ERROR", StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(index, 0, 1, "unexpected token ERROR", "E100"));
            }
            else if (lines[index].Contains("WARN", StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(index, 2, 2, "suspicious construct WARN", "W200"));
            }
        }

        await NotifyAsync("textDocument/publishDiagnostics", new JsonObject
        {
            ["uri"] = uri,
            ["diagnostics"] = diagnostics,
        }).ConfigureAwait(false);
    }

    private static JsonObject Diagnostic(int line, int character, int severity, string message, string code) => new()
    {
        ["range"] = new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
            ["end"] = new JsonObject { ["line"] = line, ["character"] = character + 1 },
        },
        ["severity"] = severity,
        ["code"] = code,
        ["source"] = "fake",
        ["message"] = message,
    };

    private Task RespondAsync(JsonNode? id, JsonNode? result) =>
        SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        });

    private Task NotifyAsync(string method, JsonNode parameters) =>
        SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        });

    private async Task SendAsync(JsonObject message)
    {
        try
        {
            await LspFrameWriter.WriteAsync(_serverToClient, message.ToJsonString(), _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped mid-write.
        }
    }

    private async Task WriteRawAsync(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        await _serverToClient.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
    }
}

/// <summary>
/// An <see cref="ILspTransport"/> backed by a <see cref="FakeLanguageServer"/> on the other end of
/// two <see cref="LoopbackStream"/>s.
/// </summary>
public sealed class FakeLspTransport : ILspTransport
{
    private readonly LoopbackStream _clientToServer = new();
    private readonly LoopbackStream _serverToClient = new();
    private readonly Task _serverLoop;
    private int _terminated;

    public FakeLspTransport(FakeServerBehavior behavior = FakeServerBehavior.Normal, TimeSpan? publishDelay = null)
    {
        Server = new FakeLanguageServer(_clientToServer, _serverToClient, behavior, publishDelay);
        _serverLoop = Server.Run();
    }

    public FakeLanguageServer Server { get; }

    public Stream Input => _clientToServer;

    public Stream Output => _serverToClient;

    public bool HasExited => _terminated != 0 || _serverLoop.IsCompleted;

    public int? ExitCode => HasExited ? 0 : null;

    public string Description => "fake-language-server";

    public string StandardErrorTail => string.Empty;

    public void Terminate()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        Server.Stop();
        _clientToServer.CompleteWriting();
        _serverToClient.CompleteWriting();
    }

    public async ValueTask DisposeAsync()
    {
        Terminate();
        try
        {
            await _serverLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // The loop is already unwinding.
        }
    }
}
