using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Cli.Modes;
using Andy.Permissions.Authorization;
using Andy.Permissions.Model;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// Unit-level proof that the two overlay adapters block a call BEFORE anything downstream runs
/// (issue #278): the executor decorator never delegates, and the authorizer decorator never asks
/// the rule engine.
/// </summary>
public class ModeGatedExecutionTests
{
    private static ModeToolGate Gate(AgentMode mode) => new(new AgentModeState(mode));

    [Fact]
    public async Task PlanMode_ShortCircuitsBeforeTheInnerExecutorIsCalled()
    {
        var inner = new RecordingExecutor();
        var executor = new ModeGatedToolExecutor(inner, Gate(AgentMode.Plan));

        var result = await executor.ExecuteAsync(
            "write_file",
            new Dictionary<string, object?> { ["file_path"] = "x.txt" });

        Assert.False(result.IsSuccessful);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task PlanMode_ShortCircuitsTheRequestOverloadToo()
    {
        var inner = new RecordingExecutor();
        var executor = new ModeGatedToolExecutor(inner, Gate(AgentMode.Plan));

        var result = await executor.ExecuteAsync(new ToolExecutionRequest
        {
            ToolId = "execute_command",
            Parameters = new Dictionary<string, object?> { ["command"] = "ls" },
        });

        Assert.False(result.IsSuccessful);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task PlanMode_LetsReadOnlyCallsThrough()
    {
        var inner = new RecordingExecutor();
        var executor = new ModeGatedToolExecutor(inner, Gate(AgentMode.Plan));

        var result = await executor.ExecuteAsync(
            "read_file",
            new Dictionary<string, object?> { ["file_path"] = "x.txt" });

        Assert.True(result.IsSuccessful);
        Assert.Equal(new[] { "read_file" }, inner.Calls);
    }

    [Fact]
    public async Task BuildMode_DelegatesEverything()
    {
        var inner = new RecordingExecutor();
        var executor = new ModeGatedToolExecutor(inner, Gate(AgentMode.Build));

        await executor.ExecuteAsync("write_file", new Dictionary<string, object?>());
        await executor.ExecuteAsync("execute_command", new Dictionary<string, object?>());

        Assert.Equal(new[] { "write_file", "execute_command" }, inner.Calls);
    }

    [Fact]
    public async Task ModeSwitch_TakesEffectOnTheNextCall()
    {
        var state = new AgentModeState(AgentMode.Plan);
        var inner = new RecordingExecutor();
        var executor = new ModeGatedToolExecutor(inner, new ModeToolGate(state));

        await executor.ExecuteAsync("write_file", new Dictionary<string, object?>());
        Assert.Empty(inner.Calls);

        state.TrySet(AgentMode.Build, ModeChangeSource.UserCommand, out _);
        await executor.ExecuteAsync("write_file", new Dictionary<string, object?>());
        Assert.Equal(new[] { "write_file" }, inner.Calls);
    }

    [Fact]
    public void Authorizer_DeniesWithoutConsultingTheRuleEngine()
    {
        var inner = new AlwaysAllowAuthorizer();
        var authorizer = new ModeGatedPermissionAuthorizer(inner, Gate(AgentMode.Plan));

        var evaluation = authorizer.Evaluate(new ToolAuthorizationContext(
            "write_file",
            new Dictionary<string, object?> { ["file_path"] = "x.txt" },
            "/tmp",
            null));

        Assert.Equal(PermissionOutcome.Deny, evaluation.Outcome);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public void Authorizer_DeniesUnclassifiedMcpTools()
    {
        var inner = new AlwaysAllowAuthorizer();
        var authorizer = new ModeGatedPermissionAuthorizer(inner, Gate(AgentMode.Plan));

        var evaluation = authorizer.Evaluate(new ToolAuthorizationContext(
            "mcp__tracker__create_ticket",
            new Dictionary<string, object?>(),
            "/tmp",
            null));

        Assert.Equal(PermissionOutcome.Deny, evaluation.Outcome);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public void Authorizer_DelegatesForAllowedTools()
    {
        var inner = new AlwaysAllowAuthorizer();
        var authorizer = new ModeGatedPermissionAuthorizer(inner, Gate(AgentMode.Plan));

        var evaluation = authorizer.Evaluate(new ToolAuthorizationContext(
            "read_file",
            new Dictionary<string, object?> { ["file_path"] = "x.txt" },
            "/tmp",
            null));

        Assert.Equal(PermissionOutcome.Allow, evaluation.Outcome);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void DenyEvaluation_AttributesTheDenialToTheManagedPolicyLayer()
    {
        var evaluation = ModeGatedPermissionAuthorizer.DenyEvaluation("write_file");

        Assert.Equal(PermissionOutcome.Deny, evaluation.Outcome);
        var resource = Assert.Single(evaluation.Resources);
        Assert.Equal(PermissionOutcome.Deny, resource.Outcome);
        Assert.Equal(PermissionLayer.Managed, resource.MatchedRule!.Layer);
    }

    [Fact]
    public void DenyEvaluation_SurvivesAToolIdThatIsNotAValidRuleTarget()
    {
        // An MCP server can expose a name the rule grammar rejects; synthesizing the deny must not
        // throw, or a malformed name would become an escape hatch.
        var evaluation = ModeGatedPermissionAuthorizer.DenyEvaluation("weird name(with parens)");

        Assert.Equal(PermissionOutcome.Deny, evaluation.Outcome);
    }

    private sealed class RecordingExecutor : IToolExecutor
    {
        public List<string> Calls { get; } = new();

#pragma warning disable CS0067 // Events are part of the interface; unused by this stub.
        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;
#pragma warning restore CS0067

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId,
            Dictionary<string, object?> parameters,
            ToolExecutionContext? context = null)
        {
            Calls.Add(toolId);
            return Task.FromResult(new ToolExecutionResult { IsSuccessful = true });
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
        {
            Calls.Add(request.ToolId);
            return Task.FromResult(new ToolExecutionResult { IsSuccessful = true });
        }

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
            => Task.FromResult<IList<string>>(new List<string>());

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(
            string toolId,
            Dictionary<string, object?> parameters)
            => Task.FromResult<ToolResourceUsage?>(null);

        public Task<int> CancelExecutionsAsync(string? toolId = null) => Task.FromResult(0);

        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions()
            => Array.Empty<RunningExecutionInfo>();

        public ToolExecutionStatistics GetStatistics() => new();
    }

    private sealed class AlwaysAllowAuthorizer : IToolPermissionAuthorizer
    {
        public int Calls { get; private set; }

        public PermissionEvaluation Evaluate(ToolAuthorizationContext context)
        {
            Calls++;
            return new PermissionEvaluation(PermissionOutcome.Allow, Array.Empty<EvaluatedResource>());
        }
    }
}
