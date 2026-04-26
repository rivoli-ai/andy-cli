using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Andy.Cli.Headless;

// NDJSON event-stream writer for the headless agent loop (AQ3,
// rivoli-ai/andy-cli#44). One JSON object per line, snake_case wire names,
// schema pinned at schema_version=1. The shape is governed by
// schemas/headless-events.v1.json — kept additive so consumers can roll
// independently.
//
// Threading: emit calls serialize on a single lock around a single
// TextWriter. The agent loop is sequential per turn; the lock guards
// against the post-tool callback racing with in-flight LLM streaming.
//
// The destination writer is owned by the caller (Console.Out for the
// default `output.stream = stdout` case, or a FileStream-wrapped writer
// for `event_sink.path` when the FIFO mode lands). Disposing the emitter
// flushes but does not close the writer.
public sealed class HeadlessEventEmitter : IDisposable
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly TextWriter _writer;
    private readonly object _writeLock = new();
    private readonly TimeProvider _clock;
    private bool _disposed;

    public HeadlessEventEmitter(TextWriter writer, TimeProvider? clock = null)
    {
        _writer = writer;
        _clock = clock ?? TimeProvider.System;
    }

    public void EmitStarted(Guid runId, string agentSlug, string modelProvider, string modelId, int toolCount)
        => Write(HeadlessEventKind.Started, new
        {
            run_id = runId,
            agent_slug = agentSlug,
            model_provider = modelProvider,
            model_id = modelId,
            tool_count = toolCount
        });

    public void EmitLlmChunk(string text, int? turn = null)
        => Write(HeadlessEventKind.LlmChunk, new { text, turn });

    public void EmitToolCallStarted(string callId, string toolName, string? argsDigest = null)
        => Write(HeadlessEventKind.ToolCallStarted, new { call_id = callId, tool_name = toolName, args_digest = argsDigest });

    public void EmitToolCallFinished(
        string callId,
        string toolName,
        bool ok,
        long durationMs,
        string? resultDigest = null,
        string? error = null)
        => Write(HeadlessEventKind.ToolCallFinished, new
        {
            call_id = callId,
            tool_name = toolName,
            ok,
            duration_ms = durationMs,
            result_digest = resultDigest,
            error
        });

    public void EmitOutputWritten(string path, long bytes)
        => Write(HeadlessEventKind.OutputWritten, new { path, bytes });

    public void EmitError(string message, bool fatal)
        => Write(HeadlessEventKind.Error, new { message, fatal });

    public void EmitFinished(int exitCode, long durationMs, int iterations)
        => Write(HeadlessEventKind.Finished, new { exit_code = exitCode, duration_ms = durationMs, iterations });

    // SHA-256 hex of canonical (snake_case) JSON for tool args / results.
    // Producers feed this into the *_digest fields rather than emitting raw
    // payloads — keeps the event stream cheap and avoids leaking secrets that
    // a tool arg or result might contain.
    public static string ComputeDigest(object? payload)
    {
        if (payload is null) return "sha256:empty";
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, s_jsonOptions);
        var hash = SHA256.HashData(json);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void Write(HeadlessEventKind kind, object data)
    {
        // Wrapping the per-event payload in a JsonObject lets the writer keep
        // the envelope shape (schema_version/ts/kind/data) explicit at the
        // serializer call site rather than sprinkling it into every Emit*.
        var envelope = new
        {
            schema_version = SchemaVersion,
            ts = _clock.GetUtcNow(),
            kind,
            data
        };

        var line = JsonSerializer.Serialize(envelope, s_jsonOptions);

        lock (_writeLock)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Flush();
        }
    }
}

public enum HeadlessEventKind
{
    Started,
    LlmChunk,
    ToolCallStarted,
    ToolCallFinished,
    OutputWritten,
    Error,
    Finished
}
