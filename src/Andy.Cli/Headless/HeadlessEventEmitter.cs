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
    private readonly HeadlessTranscriptSession? _transcript;
    private readonly object _writeLock = new();
    private readonly TimeProvider _clock;
    private bool _disposed;

    public HeadlessEventEmitter(
        TextWriter writer,
        TimeProvider? clock = null,
        HeadlessTranscriptSession? transcript = null)
    {
        _writer = writer;
        _clock = clock ?? TimeProvider.System;
        _transcript = transcript;
    }

    public void EmitStarted(Guid runId, string agentSlug, string modelProvider, string modelId, int toolCount)
        => Write(HeadlessEventKind.Started, new JsonObject
        {
            ["run_id"] = runId.ToString(),
            ["agent_slug"] = agentSlug,
            ["model_provider"] = modelProvider,
            ["model_id"] = modelId,
            ["tool_count"] = toolCount
        });

    public void EmitLlmChunk(string text, int? turn = null)
    {
        var data = new JsonObject { ["text"] = text };
        if (turn.HasValue) data["turn"] = turn.Value;
        Write(HeadlessEventKind.LlmChunk, data);
    }

    public void EmitToolCallStarted(string callId, string toolName, string? argsDigest = null)
    {
        var data = new JsonObject
        {
            ["call_id"] = callId,
            ["tool_name"] = toolName
        };
        if (argsDigest is not null) data["args_digest"] = argsDigest;
        Write(HeadlessEventKind.ToolCallStarted, data);
    }

    // #179: `outcome` distinguishes the terminal state of the ACTUAL execution
    // (success / failed / denied / cancelled / timed_out). `ok` stays as the
    // coarse boolean (ok == success); `outcome` lets a consumer tell a
    // permission denial apart from an execution failure without keying on the
    // free-form `error` string. `duration_ms` is measured start-to-finish, not
    // fabricated. See ToolCallOutcome for the closed set of values.
    public void EmitToolCallFinished(
        string callId,
        string toolName,
        bool ok,
        long durationMs,
        string? resultDigest = null,
        string? error = null,
        string? outcome = null)
    {
        var data = new JsonObject
        {
            ["call_id"] = callId,
            ["tool_name"] = toolName,
            ["ok"] = ok,
            ["duration_ms"] = durationMs
        };
        if (outcome is not null) data["outcome"] = outcome;
        if (resultDigest is not null) data["result_digest"] = resultDigest;
        if (error is not null) data["error"] = error;
        Write(HeadlessEventKind.ToolCallFinished, data);
    }

    // AX.4 (rivoli-ai/conductor#2091): end-of-run tool-usage audit. One event listing
    // the injected allow-list and, per distinct tool the agent invoked, the invocation
    // count and whether the permission engine permitted it. An external verifier (AX.10)
    // keys off this to confirm only permitted tools ran.
    public void EmitToolUsageAudit(
        IReadOnlyList<string> allowedTools,
        IReadOnlyList<ToolUsageAuditEntry> tools)
    {
        var allowed = new JsonArray();
        foreach (var tool in allowedTools) allowed.Add(JsonValue.Create(tool));

        var entries = new JsonArray();
        foreach (var tool in tools)
        {
            entries.Add((JsonNode)new JsonObject
            {
                ["tool_name"] = tool.ToolName,
                ["invocations"] = tool.Invocations,
                ["permitted"] = tool.Permitted
            });
        }

        Write(HeadlessEventKind.ToolUsageAudit, new JsonObject
        {
            ["allowed_tools"] = allowed,
            ["tools"] = entries
        });
    }

    public void EmitRequiredActionVerification(RequiredActionVerificationResult result)
        => Write(HeadlessEventKind.RequiredActionVerification, new
        {
            satisfied = result.Satisfied,
            requirements = result.Requirements.Select(requirement => new
            {
                index = requirement.Index,
                tool_name = requirement.ToolName,
                command_digest = requirement.CommandEquals is null
                    ? null
                    : ComputeDigest(requirement.CommandEquals),
                at_least = requirement.AtLeast,
                observed_matches = requirement.ObservedMatches,
                successful_matches = requirement.SuccessfulMatches,
                satisfied = requirement.Satisfied,
                calls = requirement.Calls.Select(call => new
                {
                    call_id = call.CallId,
                    outcome = call.Outcome
                })
            })
        });

    public void EmitOutputWritten(string path, long bytes)
        => Write(HeadlessEventKind.OutputWritten, new JsonObject { ["path"] = path, ["bytes"] = bytes });

    public void EmitError(string message, bool fatal)
        => Write(HeadlessEventKind.Error, new JsonObject { ["message"] = message, ["fatal"] = fatal });

    public void EmitFinished(int exitCode, long durationMs, int iterations)
    {
        var line = Serialize(
            HeadlessEventKind.Finished,
            new { exit_code = exitCode, duration_ms = durationMs, iterations });

        lock (_writeLock)
        {
            if (_disposed) return;

            var transcriptError = _transcript?.Complete(line);
            if (!string.IsNullOrWhiteSpace(transcriptError))
            {
                WritePrimary(Serialize(
                    HeadlessEventKind.Error,
                    new
                    {
                        message = transcriptError,
                        fatal = false
                    }));
            }

            WritePrimary(line);
            _writer.Flush();
        }
    }

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
        var line = Serialize(kind, data);

        lock (_writeLock)
        {
            if (_disposed) return;
            _transcript?.Capture(line);
            WritePrimary(line);
            _writer.Flush();
        }
    }

    private string Serialize(HeadlessEventKind kind, object data)
    {
        // Keep the envelope shape explicit at the serializer call site rather
        // than sprinkling it into every Emit* method.
        var envelope = new
        {
            schema_version = SchemaVersion,
            ts = _clock.GetUtcNow(),
            kind,
            data
        };

        return JsonSerializer.Serialize(envelope, s_jsonOptions);
    }

    private void WritePrimary(string line) => _writer.WriteLine(line);

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Flush();
            _transcript?.Dispose();
        }
    }
}

public enum HeadlessEventKind
{
    Started,
    LlmChunk,
    ToolCallStarted,
    ToolCallFinished,
    ToolUsageAudit,
    RequiredActionVerification,
    OutputWritten,
    Error,
    Finished
}

// AX.4 (rivoli-ai/conductor#2091): one row of the tool-usage audit — a distinct tool
// the agent invoked, how many times, and whether the permission engine permitted it.
public sealed record ToolUsageAuditEntry(string ToolName, int Invocations, bool Permitted);
