using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Lsp.Protocol;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Lsp;

/// <summary>
/// A single initialized language-server session: the handshake, full-text document
/// synchronization, and the diagnostics the server publishes back.
///
/// Only the subset of LSP that changed-file diagnostics needs is implemented (initialize,
/// didOpen/didChange/didSave, publishDiagnostics, shutdown/exit). Hover, definition, references
/// and friends are deliberately absent - see issue #282's "Later" section.
///
/// Document sync uses FULL text rather than incremental ranges. That is not a shortcut: the whole
/// point of the feature is that diagnostics describe the file exactly as it now exists ON DISK, so
/// the client reads the file back after the mutation and ships that text verbatim. Nothing has to
/// reconstruct the edit, and a formatter that rewrote the file between the edit and this call
/// cannot desynchronize the server's view.
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly LspServerDefinition _definition;
    private readonly ILspTransport _transport;
    private readonly JsonRpcConnection _connection;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<PublishWaiter>> _waiters = new(StringComparer.Ordinal);
    private readonly object _waiterSync = new();
    private bool _initialized;
    private int _disposed;

    private LspClient(
        LspServerDefinition definition,
        ILspTransport transport,
        JsonRpcConnection connection,
        string rootPath,
        ILogger? logger)
    {
        _definition = definition;
        _transport = transport;
        _connection = connection;
        RootPath = rootPath;
        _logger = logger;
    }

    public string ServerId => _definition.Id;

    public string RootPath { get; }

    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether the server is still usable.</summary>
    public bool IsAlive => _disposed == 0 && !_transport.HasExited && !_connection.Completed.IsCompleted;

    /// <summary>Malformed frames tolerated on this connection so far.</summary>
    public int MalformedMessageCount => _connection.MalformedMessageCount;

    /// <summary>Last few stderr lines from the server process, for status output.</summary>
    public string StandardErrorTail => _transport.StandardErrorTail;

    public string TransportDescription => _transport.Description;

    /// <summary>
    /// Runs the initialize handshake. Throws <see cref="LspStartupException"/> when the server does
    /// not complete it within the definition's start timeout, so a hung server becomes a reported
    /// failure instead of a stuck agent turn.
    /// </summary>
    public static async Task<LspClient> StartAsync(
        LspServerDefinition definition,
        string rootPath,
        ILspTransport transport,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var connection = new JsonRpcConnection(transport.Output, transport.Input, definition.Id, logger);
        var client = new LspClient(definition, transport, connection, rootPath, logger);

        connection.NotificationReceived += client.OnNotification;
        connection.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(definition.StartTimeoutMs);

        try
        {
            await connection.SendRequestAsync("initialize", BuildInitializeParams(definition, rootPath), timeout.Token)
                .ConfigureAwait(false);
            await connection.SendNotificationAsync("initialized", new JsonObject(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new LspStartupException(
                definition.Id,
                $"'{transport.Description}' did not complete the LSP initialize handshake within "
                + $"{definition.StartTimeoutMs}ms.{FormatStderr(transport)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new LspStartupException(
                definition.Id,
                $"'{transport.Description}' failed during the LSP initialize handshake: {ex.Message}."
                + FormatStderr(transport),
                ex);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        client._initialized = true;
        client.StartedAt = DateTimeOffset.UtcNow;
        logger?.LogInformation("LSP {Server}: initialized at {Root}", definition.Id, rootPath);
        return client;
    }

    private static string FormatStderr(ILspTransport transport)
    {
        var tail = transport.StandardErrorTail;
        return string.IsNullOrWhiteSpace(tail) ? string.Empty : $" Server stderr:\n{tail}";
    }

    /// <summary>
    /// Publishes the file's current on-disk content to the server and waits, with a hard deadline,
    /// for the diagnostics that describe it.
    ///
    /// ORDERING NOTE (rivoli-ai/andy-cli#283): the caller must have already applied any
    /// post-mutation formatting. This method reports on the exact <paramref name="text"/> it is
    /// given, which must be what a subsequent read of the file would return.
    /// </summary>
    public async Task<(LspDiagnosticsStatus Status, IReadOnlyList<LspDiagnostic> Diagnostics, string? Detail)>
        SyncAndWaitForDiagnosticsAsync(
            string absolutePath,
            string text,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        if (!IsAlive)
        {
            return (LspDiagnosticsStatus.ServerUnavailable, Array.Empty<LspDiagnostic>(), DescribeDeath());
        }

        var uri = LspUri.FromPath(absolutePath);
        var document = _documents.GetOrAdd(uri, _ => new OpenDocument());

        var waiter = new PublishWaiter();
        lock (_waiterSync)
        {
            _waiters.GetOrAdd(uri, _ => new List<PublishWaiter>()).Add(waiter);
        }

        try
        {
            int version;
            lock (document)
            {
                version = ++document.Version;
            }

            if (version == 1)
            {
                await _connection.SendNotificationAsync("textDocument/didOpen", new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["uri"] = uri,
                        ["languageId"] = _definition.EffectiveLanguageId,
                        ["version"] = version,
                        ["text"] = text,
                    },
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _connection.SendNotificationAsync("textDocument/didChange", new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["uri"] = uri,
                        ["version"] = version,
                    },
                    ["contentChanges"] = new JsonArray { new JsonObject { ["text"] = text } },
                }, cancellationToken).ConfigureAwait(false);
            }

            await _connection.SendNotificationAsync("textDocument/didSave", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["text"] = text,
            }, cancellationToken).ConfigureAwait(false);

            // The deadline is the whole safety story here: a server that never answers must cost the
            // tool call a bounded amount of time and nothing else. The delay is cancelled on every
            // exit path so a fast answer does not leave a timer (or an unobserved fault) behind.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(timeout, deadline.Token);

            var completed = await Task.WhenAny(
                waiter.Completion.Task,
                delayTask,
                _connection.Completed).ConfigureAwait(false);

            deadline.Cancel();
            _ = delayTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

            if (completed == waiter.Completion.Task)
            {
                return (LspDiagnosticsStatus.Received, await waiter.Completion.Task.ConfigureAwait(false), null);
            }

            if (completed == _connection.Completed)
            {
                return (LspDiagnosticsStatus.ServerUnavailable, Array.Empty<LspDiagnostic>(), DescribeDeath());
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return (LspDiagnosticsStatus.TimedOut, Array.Empty<LspDiagnostic>(), "cancelled");
            }

            return (LspDiagnosticsStatus.TimedOut, Array.Empty<LspDiagnostic>(),
                $"no diagnostics within {timeout.TotalMilliseconds:F0}ms");
        }
        catch (OperationCanceledException)
        {
            // A cancelled turn releases the waiter immediately; nothing is left pending.
            return (LspDiagnosticsStatus.TimedOut, Array.Empty<LspDiagnostic>(), "cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LSP {Server}: diagnostics request failed", _definition.Id);
            return (LspDiagnosticsStatus.ServerUnavailable, Array.Empty<LspDiagnostic>(), ex.Message);
        }
        finally
        {
            lock (_waiterSync)
            {
                if (_waiters.TryGetValue(uri, out var list))
                {
                    list.Remove(waiter);
                    if (list.Count == 0) _waiters.TryRemove(uri, out _);
                }
            }
            waiter.Completion.TrySetResult(Array.Empty<LspDiagnostic>());
        }
    }

    private string DescribeDeath()
    {
        var exitCode = _transport.ExitCode;
        var stderr = _transport.StandardErrorTail;
        var suffix = string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"; stderr: {stderr}";
        return exitCode is null
            ? $"server '{_definition.Id}' is not running{suffix}"
            : $"server '{_definition.Id}' exited with code {exitCode}{suffix}";
    }

    private void OnNotification(string method, JsonNode? parameters)
    {
        if (!string.Equals(method, "textDocument/publishDiagnostics", StringComparison.Ordinal)) return;
        if (parameters is not JsonObject payload) return;

        try
        {
            var uri = payload.TryGetPropertyValue("uri", out var uriNode) ? uriNode?.GetValue<string>() : null;
            if (string.IsNullOrEmpty(uri)) return;

            var diagnostics = ParseDiagnostics(payload);

            List<PublishWaiter>? waiting = null;
            lock (_waiterSync)
            {
                if (_waiters.TryGetValue(uri, out var list) && list.Count > 0)
                {
                    waiting = new List<PublishWaiter>(list);
                }
            }

            if (waiting is null) return;
            foreach (var waiter in waiting)
            {
                waiter.Completion.TrySetResult(diagnostics);
            }
        }
        catch (Exception ex)
        {
            // Malformed diagnostics from a misbehaving server must not fault the read loop.
            _logger?.LogDebug(ex, "LSP {Server}: could not parse publishDiagnostics", _definition.Id);
        }
    }

    private static IReadOnlyList<LspDiagnostic> ParseDiagnostics(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("diagnostics", out var node) || node is not JsonArray array)
        {
            return Array.Empty<LspDiagnostic>();
        }

        var result = new List<LspDiagnostic>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject diagnostic) continue;

            var message = ReadString(diagnostic, "message") ?? string.Empty;
            if (message.Length > LspLimits.MaxMessageLength)
            {
                message = message[..LspLimits.MaxMessageLength] + "...";
            }

            var severity = LspDiagnosticSeverity.Error;
            if (diagnostic.TryGetPropertyValue("severity", out var severityNode) &&
                severityNode is JsonValue severityValue &&
                severityValue.TryGetValue<int>(out var severityNumber) &&
                severityNumber is >= 1 and <= 4)
            {
                severity = (LspDiagnosticSeverity)severityNumber;
            }

            var line = 0;
            var column = 0;
            if (diagnostic.TryGetPropertyValue("range", out var rangeNode) && rangeNode is JsonObject range &&
                range.TryGetPropertyValue("start", out var startNode) && startNode is JsonObject start)
            {
                line = ReadInt(start, "line") ?? 0;
                column = ReadInt(start, "character") ?? 0;
            }

            string? code = null;
            if (diagnostic.TryGetPropertyValue("code", out var codeNode) && codeNode is JsonValue codeValue)
            {
                code = codeValue.TryGetValue<string>(out var codeText)
                    ? codeText
                    : codeValue.TryGetValue<int>(out var codeNumber)
                        ? codeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : null;
            }

            result.Add(new LspDiagnostic(
                severity,
                line + 1,
                column + 1,
                message.Replace('\r', ' ').Replace('\n', ' '),
                code,
                ReadString(diagnostic, "source")));
        }

        return result;
    }

    private static string? ReadString(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static int? ReadInt(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : null;

    private static JsonObject BuildInitializeParams(LspServerDefinition definition, string rootPath)
    {
        var parameters = new JsonObject
        {
            ["processId"] = System.Environment.ProcessId,
            ["clientInfo"] = new JsonObject { ["name"] = "andy-cli", ["version"] = "1" },
            ["rootUri"] = LspUri.FromPath(rootPath),
            ["rootPath"] = rootPath,
            ["workspaceFolders"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = LspUri.FromPath(rootPath),
                    ["name"] = System.IO.Path.GetFileName(rootPath.TrimEnd(
                        System.IO.Path.DirectorySeparatorChar,
                        System.IO.Path.AltDirectorySeparatorChar)),
                },
            },
            ["capabilities"] = new JsonObject
            {
                ["workspace"] = new JsonObject
                {
                    ["workspaceFolders"] = true,
                    ["configuration"] = true,
                },
                ["textDocument"] = new JsonObject
                {
                    ["synchronization"] = new JsonObject
                    {
                        ["dynamicRegistration"] = false,
                        ["didSave"] = true,
                        ["willSave"] = false,
                        ["willSaveWaitUntil"] = false,
                    },
                    ["publishDiagnostics"] = new JsonObject
                    {
                        ["relatedInformation"] = false,
                        ["versionSupport"] = true,
                    },
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(definition.InitializationOptionsJson))
        {
            try
            {
                parameters["initializationOptions"] = JsonNode.Parse(definition.InitializationOptionsJson!);
            }
            catch (System.Text.Json.JsonException)
            {
                // Reported by the configuration loader; an unusable blob must not block startup.
            }
        }

        return parameters;
    }

    /// <summary>
    /// Politely asks the server to shut down, then tears the process down regardless. The polite
    /// half is bounded: a server that ignores shutdown is killed, never waited on.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Release anything still waiting for diagnostics before the transport goes away.
        lock (_waiterSync)
        {
            foreach (var list in _waiters.Values)
            {
                foreach (var waiter in list)
                {
                    waiter.Completion.TrySetResult(Array.Empty<LspDiagnostic>());
                }
            }
            _waiters.Clear();
        }

        try
        {
            // Only a server that actually completed the handshake is owed a polite shutdown; one
            // that never answered initialize will not answer this either, and waiting on it would
            // just add latency to an already-failed startup.
            if (_initialized && !_transport.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _connection.SendRequestAsync("shutdown", null, timeout.Token).ConfigureAwait(false);
                await _connection.SendNotificationAsync("exit", null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // A server that will not shut down cleanly gets terminated below.
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class OpenDocument
    {
        public int Version;
    }

    private sealed class PublishWaiter
    {
        public readonly TaskCompletionSource<IReadOnlyList<LspDiagnostic>> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
