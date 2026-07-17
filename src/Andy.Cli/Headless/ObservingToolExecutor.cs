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

    // The permission decision reached BEFORE execution. Only a definitive verdict
    // from a wired authorizer is authoritative: Allow runs the tool, Deny enforces
    // a short-circuit, and Unknown (no authorizer wired, or an evaluator that could
    // not resolve) declines to make an authoritative deny and defers to the inner
    // executor's capability profile.
    private enum PermissionDecision { Allow, Deny, Unknown }

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
        var decision = EvaluateDecision(toolId, parameters, contextWorkingDirectory);
        _auditor.RecordInvocation(toolId, permitted: decision == PermissionDecision.Allow);

        // #179 (fix 3): the args digest must never prevent emission or execution.
        // A non-serializable parameter bag would otherwise throw here — before any
        // started/finished event and before the tool runs — silently dropping the
        // call. SafeDigest degrades a serialization failure to an omitted digest.
        var argsDigest = SafeDigest(parameters);
        _emitter.EmitToolCallStarted(callId, toolId, argsDigest);

        var stopwatch = Stopwatch.StartNew();

        // #179 (fix 1): a hard Deny (or, in non-interactive headless, an Ask that
        // has no broker) is ENFORCED here, not merely labeled. Short-circuit
        // WITHOUT calling execute() so a denied tool cannot run side effects while
        // being reported outcome=denied — the emitted verdict now agrees with what
        // actually executed (nothing). Only a definitive verdict from a wired
        // authorizer triggers this; an Unknown verdict defers to the inner
        // executor, whose capability profile stays the enforcement point, and is
        // reported by its ACTUAL result (never denied).
        if (decision == PermissionDecision.Deny)
        {
            stopwatch.Stop();
            _emitter.EmitToolCallFinished(
                callId,
                toolId,
                ok: false,
                durationMs: stopwatch.ElapsedMilliseconds,
                error: "permission denied",
                outcome: ToolCallOutcome.Denied);
            return new ToolExecutionResult
            {
                IsSuccessful = false,
                ErrorMessage = "permission denied",
            };
        }

        ToolExecutionResult result;
        try
        {
            result = await execute().ConfigureAwait(false);
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

        // #179 (fix 2): emit the terminal event for a completed execution OUTSIDE
        // the try above. A throw while classifying / digesting a SUCCESSFUL result
        // must not be caught by the general catch and turned into a SECOND
        // (failed) finished event that also masks the real result. Combined with
        // SafeDigest (which never throws) and the exactly-one emission on the deny,
        // cancel, and failure branches, this guarantees one finished per start.
        stopwatch.Stop();
        EmitFinish(callId, toolId, stopwatch.ElapsedMilliseconds, result);
        return result;
    }

    // Maps a COMPLETED ToolExecutionResult to a single terminal outcome. (Permission
    // denials never reach here — they short-circuit in ObserveAsync — so this method
    // only distinguishes success / failure / cancellation / timeout.)
    private void EmitFinish(
        string callId,
        string toolId,
        long durationMs,
        ToolExecutionResult result)
    {
        string outcome;
        bool ok;
        string? error;

        if (result.HitResourceLimits)
        {
            // #179 (fix 4): a per-tool resource / time cap is a RELIABLE timeout
            // signal — the tool's own limits fired — so it maps to timed_out.
            outcome = ToolCallOutcome.TimedOut;
            ok = false;
            error = FirstNonEmpty(result.ErrorMessage, "tool execution hit resource limits");
        }
        else if (result.WasCancelled)
        {
            // #179 (fix 4): WasCancelled cannot be reliably attributed to a per-tool
            // timeout versus a run-level cancel (SIGTERM / wall-clock) at this
            // boundary — HeadlessAgentRunner documents run cancellation surfacing as
            // a cancelled result — so it collapses to `cancelled`, matching a thrown
            // OperationCanceledException. Genuine per-tool timeouts arrive via
            // HitResourceLimits (handled above), keeping the two outcomes consistent.
            outcome = ToolCallOutcome.Cancelled;
            ok = false;
            error = FirstNonEmpty(result.ErrorMessage, "tool execution cancelled");
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

        // #179 (fix 2): digesting the result payload is best-effort — a
        // non-serializable Data must not turn a successful tool into a failure.
        var resultDigest = ok ? SafeDigest(result.Data) : null;

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
    // time. Only Allow is executable under the headless fail-closed contract (Ask
    // has no broker, so it is a Deny). A missing authorizer or an evaluator that
    // throws yields Unknown: this decorator will NOT claim an authoritative deny
    // (which would falsely block tools the engine's capability profile is meant to
    // gate) and instead defers enforcement to the inner executor.
    private PermissionDecision EvaluateDecision(
        string toolId,
        IReadOnlyDictionary<string, object?>? parameters,
        string? contextWorkingDirectory)
    {
        if (_authorizer is null)
        {
            return PermissionDecision.Unknown;
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
            return _authorizer.Evaluate(context).Outcome == PermissionOutcome.Allow
                ? PermissionDecision.Allow
                : PermissionDecision.Deny;
        }
        catch
        {
            // An evaluator that cannot resolve should not crash execution and must
            // not be treated as an authoritative deny; defer to inner enforcement.
            return PermissionDecision.Unknown;
        }
    }

    // Best-effort SHA-256 digest of a payload for the event stream. A serialization
    // failure (e.g. a reference cycle in a tool's args or result) degrades to an
    // omitted digest rather than throwing — the digest is a diagnostic aid, never a
    // reason to drop an event or fail a call. Null payloads omit the digest.
    private static string? SafeDigest(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return HeadlessEventEmitter.ComputeDigest(payload);
        }
        catch
        {
            return null;
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
