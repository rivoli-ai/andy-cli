using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Andy.Permissions.Authorization;
using Andy.Permissions.Model;
using Andy.Tools.Core;

namespace Andy.Cli.Headless;

// #179: emits tool_call_started / tool_call_finished around the ACTUAL tool
// execution, so a finish is reported only after the tool truly completes.
//
// Background: Andy.Engine's SimpleAgent raises its ToolCalled event BEFORE the
// tool is permission-evaluated or executed, and the engine exposes NO
// post-execution / result event. The previous CLI wiring subscribed to
// ToolCalled and immediately emitted a paired tool_call_finished with a
// fabricated ok=true / duration_ms=0 — consumers saw success while the tool was
// still running, denied, cancelled, or failing.
//
// The one signal the CLI fully owns is the IToolExecutor it hands to
// SimpleAgent: the engine calls ExecuteAsync exactly once per tool call and
// awaits the real ToolExecutionResult (or an exception). Wrapping that boundary
// gives an exact start/finish correlation with a measured duration and the real
// outcome — entirely CLI-side, no engine change required. (The remaining
// engine-side gap: SimpleAgent has no ToolCompleted/ToolResult event, so a host
// that does NOT own the executor still cannot observe completion. That is
// tracked as cross-repo work in andy-engine.)
//
// This decorator is placed OUTERMOST in the executor chain (outside
// HeadlessCapabilityToolExecutor) so the measured duration covers the whole
// execution including the permission gate, and the observed result reflects the
// gate's synthesized deny result.
public sealed class ObservingToolExecutor : IToolExecutor
{
    private readonly IToolExecutor _inner;
    private readonly HeadlessEventEmitter _emitter;
    private readonly ToolUsageAuditor _auditor;
    private readonly IToolPermissionAuthorizer? _authorizer;
    private readonly IToolRegistry? _registry;
    private readonly string? _workingDirectory;

    public ObservingToolExecutor(
        IToolExecutor inner,
        HeadlessEventEmitter emitter,
        ToolUsageAuditor auditor,
        IToolPermissionAuthorizer? authorizer,
        IToolRegistry? registry,
        string? workingDirectory)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        _authorizer = authorizer;
        _registry = registry;
        _workingDirectory = workingDirectory;
    }

    public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted
    {
        add { _inner.ExecutionStarted += value; }
        remove { _inner.ExecutionStarted -= value; }
    }

    public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted
    {
        add { _inner.ExecutionCompleted += value; }
        remove { _inner.ExecutionCompleted -= value; }
    }

    public event EventHandler<SecurityViolationEventArgs>? SecurityViolation
    {
        add { _inner.SecurityViolation += value; }
        remove { _inner.SecurityViolation -= value; }
    }

    public Task<ToolExecutionResult> ExecuteAsync(
        string toolId,
        Dictionary<string, object?> parameters,
        ToolExecutionContext? context = null)
        => ObserveAsync(
            toolId,
            parameters,
            context?.WorkingDirectory,
            () => _inner.ExecuteAsync(toolId, parameters, context));

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ObserveAsync(
            request.ToolId,
            request.Parameters,
            request.Context?.WorkingDirectory,
            () => _inner.ExecuteAsync(request));
    }

    // Runs the delegated execution while emitting exactly one tool_call_started
    // and exactly one tool_call_finished. The finished event carries the real
    // outcome and a measured duration; the auditor is updated with the actual
    // permission verdict (evaluated here with the real parameters, not later with
    // an empty bag).
    private async Task<ToolExecutionResult> ObserveAsync(
        string toolId,
        IReadOnlyDictionary<string, object?>? parameters,
        string? contextWorkingDirectory,
        Func<Task<ToolExecutionResult>> execute)
    {
        var callId = Guid.NewGuid().ToString("N")[..12];
        var permitted = EvaluateVerdict(toolId, parameters, contextWorkingDirectory);
        _auditor.RecordInvocation(toolId, permitted);

        var argsDigest = parameters is null ? null : HeadlessEventEmitter.ComputeDigest(parameters);
        _emitter.EmitToolCallStarted(callId, toolId, argsDigest);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await execute().ConfigureAwait(false);
            stopwatch.Stop();
            EmitFinish(callId, toolId, stopwatch.ElapsedMilliseconds, result, permitted);
            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _emitter.EmitToolCallFinished(
                callId,
                toolId,
                ok: false,
                durationMs: stopwatch.ElapsedMilliseconds,
                error: "cancelled",
                outcome: ToolCallOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _emitter.EmitToolCallFinished(
                callId,
                toolId,
                ok: false,
                durationMs: stopwatch.ElapsedMilliseconds,
                error: Describe(ex),
                outcome: ToolCallOutcome.Failed);
            throw;
        }
    }

    // Maps the concrete ToolExecutionResult to a single terminal outcome. A
    // permission denial (verdict != Allow, which in headless includes Ask since
    // there is no interactive broker) is never reported as success and is
    // distinguishable from an execution failure via the `outcome` field.
    private void EmitFinish(
        string callId,
        string toolId,
        long durationMs,
        ToolExecutionResult result,
        bool permitted)
    {
        string outcome;
        bool ok;
        string? error;

        if (!permitted)
        {
            outcome = ToolCallOutcome.Denied;
            ok = false;
            error = FirstNonEmpty(result.ErrorMessage, "permission denied");
        }
        else if (result.WasCancelled || result.HitResourceLimits)
        {
            // A per-tool timeout / resource-limit abort surfaces here (the tool's
            // own timeout token fired rather than throwing to the caller).
            outcome = ToolCallOutcome.TimedOut;
            ok = false;
            error = FirstNonEmpty(result.ErrorMessage, "tool execution timed out or hit resource limits");
        }
        else if (result.IsSuccessful)
        {
            outcome = ToolCallOutcome.Success;
            ok = true;
            error = null;
        }
        else
        {
            outcome = ToolCallOutcome.Failed;
            ok = false;
            error = FirstNonEmpty(result.ErrorMessage, "tool execution failed");
        }

        string? resultDigest = null;
        if (ok && result.Data is not null)
        {
            resultDigest = HeadlessEventEmitter.ComputeDigest(result.Data);
        }

        _emitter.EmitToolCallFinished(
            callId,
            toolId,
            ok,
            durationMs,
            resultDigest,
            error,
            outcome);
    }

    // Evaluates the live permission engine with the REAL parameters at execution
    // time. Only Allow counts as permitted under the headless fail-closed
    // contract (Ask has no broker, so it is denied). When the authorizer is not
    // wired the verdict is unknown and treated as not-permitted (fail-closed),
    // matching the end-of-run audit's prior behavior.
    private bool EvaluateVerdict(
        string toolId,
        IReadOnlyDictionary<string, object?>? parameters,
        string? contextWorkingDirectory)
    {
        if (_authorizer is null)
        {
            return false;
        }

        var metadata = _registry?.GetTool(toolId)?.Metadata;
        var authParameters = parameters is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(parameters);
        var context = new ToolAuthorizationContext(
            toolId,
            authParameters,
            WorkingDirectory: contextWorkingDirectory ?? _workingDirectory,
            Metadata: metadata);

        try
        {
            return _authorizer.Evaluate(context).Outcome == PermissionOutcome.Allow;
        }
        catch
        {
            // An evaluator that cannot resolve should not crash execution; treat
            // an unresolvable verdict as not-permitted.
            return false;
        }
    }

    private static string FirstNonEmpty(string? primary, string fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary!;

    private static string Describe(Exception ex)
        => $"{ex.GetType().Name}: {ex.Message}";

    public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
        => _inner.ValidateExecutionRequestAsync(request);

    public Task<ToolResourceUsage?> EstimateResourceUsageAsync(
        string toolId,
        Dictionary<string, object?> parameters)
        => _inner.EstimateResourceUsageAsync(toolId, parameters);

    public Task<int> CancelExecutionsAsync(string? toolId = null)
        => _inner.CancelExecutionsAsync(toolId ?? string.Empty);

    public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions()
        => _inner.GetRunningExecutions();

    public ToolExecutionStatistics GetStatistics()
        => _inner.GetStatistics();
}

// #179: the closed set of terminal states for a tool_call_finished event. Kept
// as plain string constants (the emitter serializes snake_case wire values) so
// the schema and consumers share one vocabulary.
public static class ToolCallOutcome
{
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}
