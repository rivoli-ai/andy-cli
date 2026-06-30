using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Tools.Core;

namespace Andy.Cli.Headless;

/// <summary>
/// Headless counterpart to <see cref="Andy.Cli.Services.UiUpdatingToolExecutor"/>'s
/// capability grant (rivoli-ai/andy-cli#157).
///
/// In the INTERACTIVE path, UiUpdatingToolExecutor.GrantGatedCapabilities sets the
/// gated capability flags (FileSystemAccess/NetworkAccess/ProcessExecution/EnvironmentAccess)
/// on every ToolExecutionContext before delegating, so the lower-level capability checks
/// (SecurityManager.ValidateExecution + ToolBase.CanExecuteWithPermissions) don't pre-empt
/// the consent gate. The engine otherwise builds the context with the restrictive default
/// profile, and a tool that declares ProcessExecution (execute_command) is rejected at
/// runtime with "Tool requires process execution but it is not granted" — even when the
/// run config's allowed_tools lists it.
///
/// The HEADLESS path (HeadlessAgentRunner → SimpleAgent → base ToolExecutor) had no such
/// grant, so execute_command stayed gated off in containers. This decorator restores the
/// grant, but UNLIKE the interactive path it is:
///   * SCOPED to the run config's allowed_tools — it only grants when execute_command is
///     actually permitted for this run; and
///   * FAIL-CLOSED — when execute_command is NOT in allowed_tools it grants NOTHING, so a
///     config that doesn't permit shell execution still blocks it.
///
/// File-write tools and run_tests are unaffected: they don't require the ProcessExecution
/// capability and are gated purely by the consent allow-list.
/// </summary>
public sealed class HeadlessCapabilityToolExecutor : IToolExecutor
{
    private readonly IToolExecutor _inner;
    private readonly bool _executeCommandAllowed;

    /// <param name="inner">The base executor to delegate to.</param>
    /// <param name="executeCommandAllowed">
    /// True iff the run config's allowed_tools contains "execute_command". When false the
    /// decorator grants no capabilities (fail-closed).
    /// </param>
    public HeadlessCapabilityToolExecutor(IToolExecutor inner, bool executeCommandAllowed)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _executeCommandAllowed = executeCommandAllowed;
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
        // Mirror the interactive path's null handling: synthesize a context so the grant has
        // somewhere to land, and pass that same instance through to the inner executor.
        context ??= new ToolExecutionContext();
        GrantGatedCapabilitiesIfAllowed(context);
        return _inner.ExecuteAsync(toolId, parameters, context);
    }

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Context != null)
        {
            GrantGatedCapabilitiesIfAllowed(request.Context);
        }

        return _inner.ExecuteAsync(request);
    }

    /// <summary>
    /// Grants the gated capability flags on the context's permission profile — but ONLY when
    /// execute_command is in this run's allowed_tools. A trusted shell implies filesystem,
    /// network, and environment access, so all four flags are granted together, mirroring
    /// UiUpdatingToolExecutor.GrantGatedCapabilities. When execute_command is not permitted
    /// this is a no-op (fail-closed): the restrictive default profile stands and the engine
    /// blocks process-executing tools as before.
    /// </summary>
    private void GrantGatedCapabilitiesIfAllowed(ToolExecutionContext context)
    {
        if (!_executeCommandAllowed)
        {
            return;
        }

        if (context.Permissions is null)
        {
            return;
        }

        context.Permissions.FileSystemAccess = true;
        context.Permissions.NetworkAccess = true;
        context.Permissions.ProcessExecution = true;
        context.Permissions.EnvironmentAccess = true;
    }

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
