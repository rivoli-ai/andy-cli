// rivoli-ai/andy-cli#157: in headless/container runs execute_command was rejected at runtime
// ("Tool requires process execution but it is not granted") even when allowed_tools listed it,
// because the headless path never granted the ProcessExecution capability on the execution
// context the way the interactive UiUpdatingToolExecutor does. HeadlessCapabilityToolExecutor
// restores that grant, SCOPED to allowed_tools and FAIL-CLOSED.
//
// These tests fail against current main (no decorator exists) and pass with it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Tools.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class HeadlessCapabilityToolExecutorTests
{
    // Captures the context the decorator hands to the inner executor so the test can inspect
    // the capability flags. Implements the full IToolExecutor surface (all members delegate to
    // nothing / return benign defaults — only ExecuteAsync is exercised).
    private sealed class CapturingToolExecutor : IToolExecutor
    {
        public ToolExecutionContext? LastContext { get; private set; }

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId,
            Dictionary<string, object?> parameters,
            ToolExecutionContext? context = null)
        {
            LastContext = context;
            return Task.FromResult(new ToolExecutionResult { IsSuccessful = true });
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
        {
            LastContext = request.Context;
            return Task.FromResult(new ToolExecutionResult { IsSuccessful = true });
        }

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
            => Task.FromResult<IList<string>>(new List<string>());

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(
            string toolId, Dictionary<string, object?> parameters)
            => Task.FromResult<ToolResourceUsage?>(null);

        public Task<int> CancelExecutionsAsync(string? toolId = null) => Task.FromResult(0);

        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions()
            => Array.Empty<RunningExecutionInfo>();

        public ToolExecutionStatistics GetStatistics() => new();

        // Keep the events "used" so the compiler doesn't warn; never raised in tests.
        public void NoOpRaise()
        {
            ExecutionStarted?.Invoke(this, null!);
            ExecutionCompleted?.Invoke(this, null!);
            SecurityViolation?.Invoke(this, null!);
        }
    }

    // Start every context from an explicit RESTRICTIVE profile so the test asserts the decorator's
    // delta, not the framework's default-context value (a bare `new ToolExecutionContext()` is more
    // permissive than the engine's per-run profile, which is the restrictive one the bug fires on).
    private static ToolExecutionContext Restricted()
    {
        var context = new ToolExecutionContext();
        context.Permissions.ProcessExecution = false;
        context.Permissions.FileSystemAccess = false;
        context.Permissions.NetworkAccess = false;
        context.Permissions.EnvironmentAccess = false;
        return context;
    }

    [Fact]
    public async Task ExecuteCommandAllowed_GrantsProcessExecution_OnContext()
    {
        var inner = new CapturingToolExecutor();
        var sut = new HeadlessCapabilityToolExecutor(inner, executeCommandAllowed: true);

        var context = Restricted();

        await sut.ExecuteAsync("execute_command", new Dictionary<string, object?>(), context);

        Assert.NotNull(inner.LastContext);
        Assert.True(inner.LastContext!.Permissions.ProcessExecution);
        // A trusted shell implies the rest, mirroring UiUpdatingToolExecutor.GrantGatedCapabilities.
        Assert.True(inner.LastContext.Permissions.FileSystemAccess);
        Assert.True(inner.LastContext.Permissions.NetworkAccess);
        Assert.True(inner.LastContext.Permissions.EnvironmentAccess);
    }

    [Fact]
    public async Task ExecuteCommandNotAllowed_GrantsNothing_FailClosed()
    {
        var inner = new CapturingToolExecutor();
        var sut = new HeadlessCapabilityToolExecutor(inner, executeCommandAllowed: false);

        var context = Restricted();

        await sut.ExecuteAsync("execute_command", new Dictionary<string, object?>(), context);

        Assert.NotNull(inner.LastContext);
        // Fail-closed: a config that doesn't allow execute_command must still block it — the
        // decorator must NOT flip ProcessExecution on.
        Assert.False(inner.LastContext!.Permissions.ProcessExecution);
        Assert.False(inner.LastContext.Permissions.FileSystemAccess);
        Assert.False(inner.LastContext.Permissions.NetworkAccess);
        Assert.False(inner.LastContext.Permissions.EnvironmentAccess);
    }

    [Fact]
    public async Task NullContext_IsSynthesized_AndGrantedWhenAllowed()
    {
        var inner = new CapturingToolExecutor();
        var sut = new HeadlessCapabilityToolExecutor(inner, executeCommandAllowed: true);

        // Mirror the interactive path: a null context must not throw; it is synthesized and granted.
        await sut.ExecuteAsync("execute_command", new Dictionary<string, object?>(), context: null);

        Assert.NotNull(inner.LastContext);
        Assert.True(inner.LastContext!.Permissions.ProcessExecution);
    }

    [Fact]
    public async Task RequestOverload_GrantsProcessExecution_WhenAllowed()
    {
        var inner = new CapturingToolExecutor();
        var sut = new HeadlessCapabilityToolExecutor(inner, executeCommandAllowed: true);

        var context = Restricted();
        var request = new ToolExecutionRequest
        {
            ToolId = "execute_command",
            Parameters = new Dictionary<string, object?>(),
            Context = context,
        };

        await sut.ExecuteAsync(request);

        Assert.NotNull(inner.LastContext);
        Assert.True(inner.LastContext!.Permissions.ProcessExecution);
    }

    // Integration-style: drive execute_command through the REAL headless executor wiring and
    // assert the run's allowed_tools-scoped capability grant authorizes it (no "process
    // execution ... not granted" rejection). Against current main (no decorator) the same call
    // is rejected — that is the bug this fixes.
    private static HeadlessRunConfig ExecConfig(IReadOnlyList<string> allowedTools)
        => new()
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent { Slug = "exec-agent", Instructions = "stub" },
            Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = System.IO.Path.GetTempPath() },
            Output = new HeadlessOutput
            {
                File = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "exec-out.txt"),
                Stream = "stdout",
            },
            Permissions = new HeadlessPermissions { AllowedTools = allowedTools },
            Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 30 },
        };

    [Fact]
    public async Task RealExecutor_WithDecorator_AuthorizesExecuteCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // echo invocation below is POSIX-shaped.
        }

        using var services = HeadlessAgentRunner.BuildServiceProvider(
            ExecConfig(new[] { "execute_command" }), NullLoggerFactory.Instance);
        HeadlessAgentRunner.RegisterBuiltInTools(services, NullLoggerFactory.Instance);

        var baseExecutor = services.GetRequiredService<IToolExecutor>();
        var executor = new HeadlessCapabilityToolExecutor(baseExecutor, executeCommandAllowed: true);

        var context = new ToolExecutionContext { WorkingDirectory = System.IO.Path.GetTempPath() };
        var result = await executor.ExecuteAsync(
            "execute_command",
            new Dictionary<string, object?> { ["command"] = "echo andy157" },
            context);

        var combined = $"{result.Message} {result.ErrorMessage}";
        Assert.DoesNotContain("process execution", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealExecutor_WithoutDecorator_RejectsExecuteCommand_ProvingTheGap()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var services = HeadlessAgentRunner.BuildServiceProvider(
            ExecConfig(new[] { "execute_command" }), NullLoggerFactory.Instance);
        HeadlessAgentRunner.RegisterBuiltInTools(services, NullLoggerFactory.Instance);

        // The BARE executor (what headless used before #157) — no capability grant.
        var baseExecutor = services.GetRequiredService<IToolExecutor>();

        var context = new ToolExecutionContext { WorkingDirectory = System.IO.Path.GetTempPath() };
        var result = await baseExecutor.ExecuteAsync(
            "execute_command",
            new Dictionary<string, object?> { ["command"] = "echo andy157" },
            context);

        var combined = $"{result.Message} {result.ErrorMessage}";
        Assert.False(result.IsSuccessful);
        Assert.Contains("process execution", combined, StringComparison.OrdinalIgnoreCase);
    }
}
