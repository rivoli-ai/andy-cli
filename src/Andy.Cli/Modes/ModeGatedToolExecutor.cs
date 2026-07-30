using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Tools.Core;

namespace Andy.Cli.Modes;

/// <summary>
/// Outermost <see cref="IToolExecutor"/> decorator: refuses a tool call the active mode forbids
/// WITHOUT calling the inner executor, so the denial happens strictly before execution and before
/// the permission engine ever evaluates a rule.
///
/// Ordering matters and is deliberate. <c>AddAndyCliPermissions</c> installs this decorator AFTER
/// the permission engine has decorated <see cref="IToolExecutor"/>, which makes this the outer
/// wrapper: mode policy runs first, and the permission engine (with its user / project / local /
/// session / injected layers) only ever sees calls the mode already permitted. That is what makes
/// an existing <c>write_file(*)</c> Allow rule powerless in Plan mode.
/// </summary>
public sealed class ModeGatedToolExecutor : IToolExecutor
{
    private readonly IToolExecutor _inner;
    private readonly ModeToolGate _gate;

    public ModeGatedToolExecutor(IToolExecutor inner, ModeToolGate gate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
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
    {
        var verdict = _gate.Evaluate(toolId, parameters);
        return verdict.Allowed
            ? _inner.ExecuteAsync(toolId, parameters, context)
            : Task.FromResult(Denied(verdict));
    }

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var verdict = _gate.Evaluate(request.ToolId, request.Parameters);
        return verdict.Allowed
            ? _inner.ExecuteAsync(request)
            : Task.FromResult(Denied(verdict));
    }

    /// <summary>
    /// The synthesized result a blocked call returns. It is a normal unsuccessful result (not an
    /// exception) so the agent loop reports the denial to the model and can keep planning.
    /// </summary>
    internal static ToolExecutionResult Denied(ModeToolVerdict verdict) => new()
    {
        IsSuccessful = false,
        ErrorMessage = verdict.Reason ?? "Denied by the active mode policy.",
    };

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
