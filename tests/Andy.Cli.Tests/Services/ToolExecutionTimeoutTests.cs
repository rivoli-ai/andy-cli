using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// The framework executor cancels every tool after context.ResourceLimits.MaxExecutionTimeMs, which
/// the engine leaves at its 30s default - so long commands (builds, test runs) were killed at ~30s
/// regardless of execute_command's timeout_seconds. UiUpdatingToolExecutor now raises that cap before
/// dispatch so the tool's own timeout governs.
/// </summary>
public class ToolExecutionTimeoutTests
{
    /// <summary>Inner executor that records the context it is dispatched with.</summary>
    private sealed class CapturingExecutor : IToolExecutor
    {
        public int? CapturedMaxExecMs;

        public Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
        {
            CapturedMaxExecMs = context?.ResourceLimits?.MaxExecutionTimeMs;
            return Task.FromResult(new ToolExecutionResult
            {
                IsSuccessful = true,
                Data = new Dictionary<string, object?> { ["stdout"] = "ok", ["exit_code"] = 0 },
            });
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) => ExecuteAsync(request.ToolId, request.Parameters, request.Context);
        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request) => Task.FromResult((IList<string>)new List<string>());
        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters) => Task.FromResult<ToolResourceUsage?>(null);
        public Task<int> CancelExecutionsAsync(string correlationId) => Task.FromResult(0);
        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions() => Array.Empty<RunningExecutionInfo>();
        public ToolExecutionStatistics GetStatistics() => new();
        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted { add { } remove { } }
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted { add { } remove { } }
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation { add { } remove { } }
    }

    [Fact]
    public async Task RaisesShortFrameworkExecutionCap_SoLongToolsAreNotKilledAt30s()
    {
        var spy = new CapturingExecutor();
        var exec = new UiUpdatingToolExecutor(spy);
        var ctx = new ToolExecutionContext { ResourceLimits = new ToolResourceLimits { MaxExecutionTimeMs = 30_000 } };

        await exec.ExecuteAsync("execute_command", new Dictionary<string, object?> { ["command"] = "echo hi" }, ctx);

        Assert.NotNull(spy.CapturedMaxExecMs);
        Assert.True(spy.CapturedMaxExecMs >= 30 * 60 * 1000,
            $"the 30s framework cap should be raised to a generous backstop, got {spy.CapturedMaxExecMs}ms");
    }

    [Fact]
    public async Task RaisedCap_IsNeverShorterThanExplicitTimeoutSeconds()
    {
        var spy = new CapturingExecutor();
        var exec = new UiUpdatingToolExecutor(spy);
        var ctx = new ToolExecutionContext { ResourceLimits = new ToolResourceLimits { MaxExecutionTimeMs = 30_000 } };

        // A caller asking for a 1-hour command must not be cut short by the framework cap.
        await exec.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "long", ["timeout_seconds"] = 3600 }, ctx);

        Assert.NotNull(spy.CapturedMaxExecMs);
        Assert.True(spy.CapturedMaxExecMs >= 3600 * 1000,
            $"cap should be >= the requested timeout_seconds, got {spy.CapturedMaxExecMs}ms");
    }
}
