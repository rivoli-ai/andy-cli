// rivoli-ai/andy-cli#179: tool_call_finished must be emitted ONLY after the
// tool's actual execution completes, with the real outcome and a measured
// duration — not fabricated ok=true/duration_ms=0 the instant the LLM emits a
// tool call. ObservingToolExecutor wraps the IToolExecutor the CLI hands to
// SimpleAgent (the exact once-per-call execution boundary) and emits the paired
// started/finished around the real result or exception.
//
// These tests drive the decorator with fake executor results / exceptions and a
// fake permission authorizer, then parse the NDJSON event stream to assert the
// outcome, non-zero measured duration, matching call ids, and exactly-once
// finish for successful, failed, denied, timed-out, and cancelled tools.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Headless;
using Andy.Permissions.Authorization;
using Andy.Permissions.Model;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class ObservingToolExecutorTests
{
    // Fake executor: returns a preset result, or throws a preset exception, after
    // an optional delay so the measured duration is observably > 0.
    private sealed class FakeExecutor : IToolExecutor
    {
        private readonly Func<Task<ToolExecutionResult>> _behavior;

        public FakeExecutor(Func<Task<ToolExecutionResult>> behavior) => _behavior = behavior;

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
            => _behavior();

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) => _behavior();

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
            => Task.FromResult<IList<string>>(new List<string>());

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(
            string toolId, Dictionary<string, object?> parameters)
            => Task.FromResult<ToolResourceUsage?>(null);

        public Task<int> CancelExecutionsAsync(string? toolId = null) => Task.FromResult(0);

        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions() => Array.Empty<RunningExecutionInfo>();

        public ToolExecutionStatistics GetStatistics() => new();

        public void NoOpRaise()
        {
            ExecutionStarted?.Invoke(this, null!);
            ExecutionCompleted?.Invoke(this, null!);
            SecurityViolation?.Invoke(this, null!);
        }
    }

    // Fake authorizer: Allow for tools in the allow-set, Deny otherwise.
    private sealed class FakeAuthorizer : IToolPermissionAuthorizer
    {
        private readonly HashSet<string> _allowed;

        public FakeAuthorizer(params string[] allowed)
            => _allowed = new HashSet<string>(allowed, StringComparer.Ordinal);

        public PermissionEvaluation Evaluate(ToolAuthorizationContext context)
        {
            var outcome = _allowed.Contains(context.ToolId)
                ? PermissionOutcome.Allow
                : PermissionOutcome.Deny;
            return new PermissionEvaluation(outcome, Array.Empty<EvaluatedResource>());
        }
    }

    private sealed record ParsedEvent(string Kind, JsonElement Data);

    private static List<ParsedEvent> Parse(StringWriter sink)
    {
        var events = new List<ParsedEvent>();
        foreach (var line in sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            // Clone so the element survives the JsonDocument dispose.
            events.Add(new ParsedEvent(
                root.GetProperty("kind").GetString()!,
                root.GetProperty("data").Clone()));
        }

        return events;
    }

    private static ObservingToolExecutor Build(
        IToolExecutor inner,
        out StringWriter sink,
        out ToolUsageAuditor auditor,
        IToolPermissionAuthorizer? authorizer)
    {
        sink = new StringWriter();
        var emitter = new HeadlessEventEmitter(sink);
        auditor = new ToolUsageAuditor();
        return new ObservingToolExecutor(inner, emitter, auditor, authorizer, registry: null, workingDirectory: null);
    }

    [Fact]
    public async Task SuccessfulTool_EmitsSuccess_NonZeroDuration_MatchingCallIds()
    {
        var inner = new FakeExecutor(async () =>
        {
            await Task.Delay(15);
            return new ToolExecutionResult { IsSuccessful = true, Data = "ok" };
        });
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("read_file"));

        var result = await sut.ExecuteAsync("read_file", new Dictionary<string, object?>(), new ToolExecutionContext());

        Assert.True(result.IsSuccessful);
        var events = Parse(sink);
        var started = Assert.Single(events, e => e.Kind == "tool_call_started");
        var finished = Assert.Single(events, e => e.Kind == "tool_call_finished");

        Assert.Equal(
            started.Data.GetProperty("call_id").GetString(),
            finished.Data.GetProperty("call_id").GetString());
        Assert.True(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("success", finished.Data.GetProperty("outcome").GetString());
        Assert.True(finished.Data.GetProperty("duration_ms").GetInt64() > 0);
    }

    [Fact]
    public async Task FailedTool_EmitsFailed_OkFalse()
    {
        var inner = new FakeExecutor(() => Task.FromResult(
            new ToolExecutionResult { IsSuccessful = false, ErrorMessage = "boom" }));
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("write_file"));

        await sut.ExecuteAsync("write_file", new Dictionary<string, object?>(), new ToolExecutionContext());

        var finished = Assert.Single(Parse(sink), e => e.Kind == "tool_call_finished");
        Assert.False(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("failed", finished.Data.GetProperty("outcome").GetString());
        Assert.Equal("boom", finished.Data.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeniedTool_EmitsDenied_NotSuccess_DistinctFromFailure()
    {
        // Even if the inner executor were to return success, a not-Allow verdict
        // means the call is denied and must never be reported ok=true.
        var inner = new FakeExecutor(() => Task.FromResult(
            new ToolExecutionResult { IsSuccessful = false, ErrorMessage = "permission denied" }));
        var sut = Build(inner, out var sink, out var auditor, new FakeAuthorizer(/* nothing allowed */));

        await sut.ExecuteAsync("delete_file", new Dictionary<string, object?>(), new ToolExecutionContext());

        var finished = Assert.Single(Parse(sink), e => e.Kind == "tool_call_finished");
        Assert.False(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("denied", finished.Data.GetProperty("outcome").GetString());

        // Auditor records the ACTUAL verdict from the execution path.
        var entry = Assert.Single(auditor.BuildEntries(authorizer: null, registry: null));
        Assert.Equal("delete_file", entry.ToolName);
        Assert.False(entry.Permitted);
    }

    [Fact]
    public async Task TimedOutTool_EmitsTimedOut()
    {
        // #179 (fix 4): a per-tool resource / time cap (HitResourceLimits) is the
        // reliable timeout signal and maps to timed_out.
        var inner = new FakeExecutor(() => Task.FromResult(
            new ToolExecutionResult { IsSuccessful = false, HitResourceLimits = true }));
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("execute_command"));

        await sut.ExecuteAsync("execute_command", new Dictionary<string, object?>(), new ToolExecutionContext());

        var finished = Assert.Single(Parse(sink), e => e.Kind == "tool_call_finished");
        Assert.False(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("timed_out", finished.Data.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task CancelledResult_EmitsCancelled_NotTimedOut()
    {
        // #179 (fix 4): a result flagged WasCancelled cannot be reliably attributed
        // to a per-tool timeout versus a run-level cancel at this boundary, so it
        // collapses to `cancelled` (consistent with a thrown OperationCanceledException),
        // NOT timed_out.
        var inner = new FakeExecutor(() => Task.FromResult(
            new ToolExecutionResult { IsSuccessful = false, WasCancelled = true }));
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("execute_command"));

        await sut.ExecuteAsync("execute_command", new Dictionary<string, object?>(), new ToolExecutionContext());

        var finished = Assert.Single(Parse(sink), e => e.Kind == "tool_call_finished");
        Assert.False(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("cancelled", finished.Data.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task CancelledTool_EmitsCancelled_ExactlyOnceFinish_AndRethrows()
    {
        var inner = new FakeExecutor(() => throw new OperationCanceledException());
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("read_file"));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ExecuteAsync("read_file", new Dictionary<string, object?>(), new ToolExecutionContext()));

        var events = Parse(sink);
        Assert.Single(events, e => e.Kind == "tool_call_started");
        var finished = Assert.Single(events, e => e.Kind == "tool_call_finished");
        Assert.False(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("cancelled", finished.Data.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ThrowingTool_EmitsFailed_ExactlyOnceFinish_AndRethrows()
    {
        var inner = new FakeExecutor(() => throw new InvalidOperationException("kaboom"));
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("some_tool"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ExecuteAsync("some_tool", new Dictionary<string, object?>(), new ToolExecutionContext()));

        var finished = Assert.Single(Parse(sink), e => e.Kind == "tool_call_finished");
        Assert.False(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("failed", finished.Data.GetProperty("outcome").GetString());
        Assert.Contains("kaboom", finished.Data.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ExactlyOneFinishPerStart_AcrossMultipleCalls()
    {
        var inner = new FakeExecutor(() => Task.FromResult(new ToolExecutionResult { IsSuccessful = true }));
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("read_file"));

        await sut.ExecuteAsync("read_file", new Dictionary<string, object?>(), new ToolExecutionContext());
        await sut.ExecuteAsync("read_file", new Dictionary<string, object?>(), new ToolExecutionContext());
        await sut.ExecuteAsync("read_file", new Dictionary<string, object?>(), new ToolExecutionContext());

        var events = Parse(sink);
        var starts = events.Where(e => e.Kind == "tool_call_started").ToList();
        var finishes = events.Where(e => e.Kind == "tool_call_finished").ToList();
        Assert.Equal(3, starts.Count);
        Assert.Equal(3, finishes.Count);

        // Every finish pairs with a distinct start id.
        var startIds = starts.Select(e => e.Data.GetProperty("call_id").GetString()).ToHashSet();
        var finishIds = finishes.Select(e => e.Data.GetProperty("call_id").GetString()).ToHashSet();
        Assert.Equal(startIds, finishIds);
    }

    [Fact]
    public async Task RequestOverload_AlsoEmitsPairedStartFinish()
    {
        var inner = new FakeExecutor(() => Task.FromResult(new ToolExecutionResult { IsSuccessful = true }));
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("read_file"));

        var request = new ToolExecutionRequest
        {
            ToolId = "read_file",
            Parameters = new Dictionary<string, object?>(),
            Context = new ToolExecutionContext(),
        };
        await sut.ExecuteAsync(request);

        var events = Parse(sink);
        Assert.Single(events, e => e.Kind == "tool_call_started");
        Assert.Single(events, e => e.Kind == "tool_call_finished");
    }

    [Fact]
    public async Task DeniedTool_DoesNotInvokeInnerExecutor_AndEmitsDenied()
    {
        // #179 (fix 1): a hard Deny is ENFORCED, not merely labeled. The inner
        // executor must never run (no side effect) and the emitted verdict
        // (outcome=denied, ok=false) must agree with the fact that nothing ran.
        var invoked = false;
        var inner = new FakeExecutor(() =>
        {
            invoked = true;
            return Task.FromResult(new ToolExecutionResult { IsSuccessful = true, Data = "ran" });
        });
        var sut = Build(inner, out var sink, out var auditor, new FakeAuthorizer(/* nothing allowed */));

        var result = await sut.ExecuteAsync(
            "delete_file", new Dictionary<string, object?>(), new ToolExecutionContext());

        Assert.False(invoked); // the tool did NOT execute
        Assert.False(result.IsSuccessful);

        var events = Parse(sink);
        Assert.Single(events, e => e.Kind == "tool_call_started");
        var finished = Assert.Single(events, e => e.Kind == "tool_call_finished");
        Assert.False(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("denied", finished.Data.GetProperty("outcome").GetString());

        var entry = Assert.Single(auditor.BuildEntries(authorizer: null, registry: null));
        Assert.False(entry.Permitted);
    }

    [Fact]
    public async Task AllowedTool_InvokesInnerExecutor_AndEmitsSuccess()
    {
        // #179 (fix 1) counterpart: an Allow verdict runs the tool and reports success.
        var invoked = false;
        var inner = new FakeExecutor(() =>
        {
            invoked = true;
            return Task.FromResult(new ToolExecutionResult { IsSuccessful = true, Data = "ran" });
        });
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("read_file"));

        await sut.ExecuteAsync("read_file", new Dictionary<string, object?>(), new ToolExecutionContext());

        Assert.True(invoked);
        var finished = Assert.Single(Parse(sink), e => e.Kind == "tool_call_finished");
        Assert.True(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("success", finished.Data.GetProperty("outcome").GetString());
    }

    // A payload with a reference cycle: System.Text.Json throws when serializing it,
    // exercising the digest guard (#179 fix 2 / fix 3).
    private sealed class Cyclic
    {
        public Cyclic? Self { get; set; }
    }

    [Fact]
    public async Task NonSerializableResultData_StillEmitsExactlyOneSuccess()
    {
        // #179 (fix 2): digesting a non-serializable result Data must be non-fatal.
        // A successful tool must NOT be turned into a failure, and there must be
        // exactly one finished=success (no second, failed event from the catch).
        var cyclic = new Cyclic();
        cyclic.Self = cyclic;
        var inner = new FakeExecutor(() => Task.FromResult(
            new ToolExecutionResult { IsSuccessful = true, Data = cyclic }));
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("read_file"));

        var result = await sut.ExecuteAsync(
            "read_file", new Dictionary<string, object?>(), new ToolExecutionContext());

        Assert.True(result.IsSuccessful);
        var events = Parse(sink);
        Assert.Single(events, e => e.Kind == "tool_call_started");
        var finished = Assert.Single(events, e => e.Kind == "tool_call_finished");
        Assert.True(finished.Data.GetProperty("ok").GetBoolean());
        Assert.Equal("success", finished.Data.GetProperty("outcome").GetString());
        // The digest is omitted (not present) when it cannot be computed.
        Assert.False(finished.Data.TryGetProperty("result_digest", out var digest)
            && digest.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task NonSerializableArgs_StillEmitsStartFinish_AndExecutes()
    {
        // #179 (fix 3): a non-serializable args bag must never prevent the started
        // event, the finished event, or the execution itself.
        var invoked = false;
        var inner = new FakeExecutor(() =>
        {
            invoked = true;
            return Task.FromResult(new ToolExecutionResult { IsSuccessful = true });
        });
        var sut = Build(inner, out var sink, out _, new FakeAuthorizer("read_file"));

        var cyclic = new Cyclic();
        cyclic.Self = cyclic;
        var parameters = new Dictionary<string, object?> { ["payload"] = cyclic };

        await sut.ExecuteAsync("read_file", parameters, new ToolExecutionContext());

        Assert.True(invoked);
        var events = Parse(sink);
        var started = Assert.Single(events, e => e.Kind == "tool_call_started");
        var finished = Assert.Single(events, e => e.Kind == "tool_call_finished");
        Assert.Equal("success", finished.Data.GetProperty("outcome").GetString());
        // args_digest is omitted when it cannot be computed, but the call still runs.
        Assert.False(started.Data.TryGetProperty("args_digest", out var digest)
            && digest.ValueKind != JsonValueKind.Null);
    }
}
