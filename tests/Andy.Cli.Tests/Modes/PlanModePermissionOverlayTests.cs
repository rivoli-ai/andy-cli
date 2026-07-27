using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.HeadlessConfig;
using Andy.Cli.Modes;
using Andy.Cli.Services;
using Andy.Permissions.Authorization;
using Andy.Permissions.Model;
using Andy.Permissions.Prompt;
using Andy.Permissions.Store;
using Andy.Tools.Core;
using Andy.Tools.Core.OutputLimiting;
using Andy.Tools.Discovery;
using Andy.Tools.Execution;
using Andy.Tools.Framework;
using Andy.Tools.Registry;
using Andy.Tools.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// End-to-end enforcement tests for the Plan-mode permission overlay (issue #278).
///
/// These build the SAME service graph the CLI wires up (<see cref="CliPermissionServiceExtensions
/// .AddAndyCliPermissions"/>) with the file-backed permission layers isolated, then run real tool
/// calls through the resolved <see cref="IToolExecutor"/>. They are the tests that prove the deny
/// happens BEFORE execution and that no allow rule at any layer can bypass it.
/// </summary>
public sealed class PlanModePermissionOverlayTests : IDisposable
{
    private readonly string _workspace;

    public PlanModePermissionOverlayTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "andy-plan-mode-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Test hygiene only.
        }
    }

    /// <summary>Builds the CLI's real permission-gated tool graph with isolated on-disk layers.</summary>
    private ServiceProvider BuildProvider(
        AgentModeState modeState,
        IPermissionPrompt? prompt = null,
        PlanModeGrantStore? grants = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(modeState);
        if (prompt is not null)
        {
            services.AddSingleton(prompt);
        }

        // Pre-registering the grant store wins over the TryAdd inside AddAndyCliPermissions. It is
        // always registered (rooted in the throwaway workspace) so no test can be influenced by the
        // developer's real .andy/modes.json or by the process working directory.
        services.AddSingleton(grants ?? new PlanModeGrantStore(_workspace, _workspace));

        services.AddSingleton<IToolValidator, ToolValidator>();
        services.AddSingleton<IToolRegistry, Andy.Tools.Registry.ToolRegistry>();
        services.AddSingleton<IToolDiscovery, ToolDiscoveryService>();
        services.AddSingleton<ISecurityManager, SecurityManager>();
        services.AddSingleton<IResourceMonitor, ResourceMonitor>();
        services.AddSingleton<IToolOutputLimiter, ToolOutputLimiter>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IPermissionProfileService, PermissionProfileService>();
        services.AddSingleton(new ToolFrameworkOptions
        {
            RegisterBuiltInTools = false,
            EnableObservability = false,
            AutoDiscoverTools = false,
        });

        ToolCatalog.RegisterAllTools(services);

        // The CLI's own permission wiring, including the mode overlay. File layers are nulled so the
        // developer's real ~/.andy rules cannot influence the result.
        CliPermissionServiceExtensions.AddAndyCliPermissions(services, interactiveBroker: null, configureStore: o =>
        {
            o.UserFilePath = null;
            o.ProjectFilePath = null;
            o.LocalFilePath = null;
            o.ManagedFilePath = null;
        });

        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IToolRegistry>();
        foreach (var reg in sp.GetServices<ToolRegistrationInfo>())
        {
            registry.RegisterTool(reg.ToolType, reg.Configuration);
        }

        return sp;
    }

    /// <summary>The smallest valid headless run config; only the DI graph is under test here.</summary>
    private static HeadlessRunConfig MinimalHeadlessConfig() => new()
    {
        SchemaVersion = 1,
        RunId = Guid.NewGuid(),
        Agent = new HeadlessAgent { Slug = "mode-agent", Instructions = "stub" },
        Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
        Tools = Array.Empty<HeadlessTool>(),
        Workspace = new HeadlessWorkspace { Root = Path.GetTempPath() },
        Output = new HeadlessOutput
        {
            File = Path.Combine(Path.GetTempPath(), "mode-out.txt"),
            Stream = "stdout",
        },
        Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 30 },
    };

    private ToolExecutionContext Context() => new()
    {
        WorkingDirectory = _workspace,
        Permissions = new ToolPermissions
        {
            FileSystemAccess = true,
            NetworkAccess = true,
            ProcessExecution = true,
            EnvironmentAccess = true,
        },
    };

    [Fact]
    public async Task PlanMode_DeniesWriteFile_BeforeItTouchesDisk()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode);
        var executor = sp.GetRequiredService<IToolExecutor>();
        var target = Path.Combine(_workspace, "should-not-exist.txt");

        var result = await executor.ExecuteAsync(
            "write_file",
            new Dictionary<string, object?> { ["file_path"] = target, ["content"] = "nope" },
            Context());

        Assert.False(result.IsSuccessful);
        Assert.Contains("Plan mode", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task PlanMode_DeniesWriteFile_EvenWithSessionAllowRule()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode);

        // The broadest possible session grant - exactly what "Allow (session)" installs.
        var store = sp.GetRequiredService<IPermissionStore>();
        store.AddSessionRule(PermissionRule.Parse("write_file(*)", PermissionOutcome.Allow, PermissionLayer.Session));

        var executor = sp.GetRequiredService<IToolExecutor>();
        var target = Path.Combine(_workspace, "session-allow.txt");

        var result = await executor.ExecuteAsync(
            "write_file",
            new Dictionary<string, object?> { ["file_path"] = target, ["content"] = "nope" },
            Context());

        Assert.False(result.IsSuccessful);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task PlanMode_DeniesWriteFile_EvenWithInjectedAllowList()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode);

        // The per-run injected allow-list sits at the highest rule layer the CLI can install.
        CliPermissionServiceExtensions.ApplyInjectedAllowList(
            sp, new[] { "write_file", "execute_command", "delete_file" });

        var executor = sp.GetRequiredService<IToolExecutor>();
        var target = Path.Combine(_workspace, "injected-allow.txt");

        var result = await executor.ExecuteAsync(
            "write_file",
            new Dictionary<string, object?> { ["file_path"] = target, ["content"] = "nope" },
            Context());

        Assert.False(result.IsSuccessful);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task PlanMode_DeniesShellCommand_EvenAReadOnlyOne()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode, new AlwaysAllowPrompt());
        CliPermissionServiceExtensions.ApplyInjectedAllowList(sp, new[] { "execute_command" });

        var executor = sp.GetRequiredService<IToolExecutor>();
        var result = await executor.ExecuteAsync(
            "execute_command",
            new Dictionary<string, object?> { ["command"] = "echo hello" },
            Context());

        Assert.False(result.IsSuccessful);
        Assert.Contains("shell", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanMode_DeniesIndirectMutationThroughShell()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode, new AlwaysAllowPrompt());
        CliPermissionServiceExtensions.ApplyInjectedAllowList(sp, new[] { "execute_command" });

        var executor = sp.GetRequiredService<IToolExecutor>();
        var target = Path.Combine(_workspace, "via-shell.txt");

        var result = await executor.ExecuteAsync(
            "execute_command",
            new Dictionary<string, object?> { ["command"] = $"echo pwned > {target}" },
            Context());

        Assert.False(result.IsSuccessful);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task PlanMode_AllowsReadFile()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode);
        var source = Path.Combine(_workspace, "readable.txt");
        File.WriteAllText(source, "hello from disk");

        var executor = sp.GetRequiredService<IToolExecutor>();
        var result = await executor.ExecuteAsync(
            "read_file",
            new Dictionary<string, object?> { ["file_path"] = source },
            Context());

        Assert.True(result.IsSuccessful, result.ErrorMessage);
    }

    [Fact]
    public async Task PlanMode_AllowsListDirectory()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode);
        File.WriteAllText(Path.Combine(_workspace, "a.txt"), "a");

        var executor = sp.GetRequiredService<IToolExecutor>();
        var result = await executor.ExecuteAsync(
            "list_directory",
            new Dictionary<string, object?> { ["directory_path"] = _workspace },
            Context());

        Assert.True(result.IsSuccessful, result.ErrorMessage);
    }

    [Fact]
    public async Task BuildMode_AllowsWriteFile_WhenAllowedByRules()
    {
        var mode = new AgentModeState(AgentMode.Build);
        using var sp = BuildProvider(mode);
        CliPermissionServiceExtensions.ApplyInjectedAllowList(sp, new[] { "write_file" });

        var executor = sp.GetRequiredService<IToolExecutor>();
        var target = Path.Combine(_workspace, "build-mode.txt");

        var result = await executor.ExecuteAsync(
            "write_file",
            new Dictionary<string, object?> { ["file_path"] = target, ["content"] = "written" },
            Context());

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task SwitchingToBuildMode_RestoresMutationOnTheSameProvider()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode);
        CliPermissionServiceExtensions.ApplyInjectedAllowList(sp, new[] { "write_file" });

        var executor = sp.GetRequiredService<IToolExecutor>();
        var target = Path.Combine(_workspace, "after-switch.txt");
        var parameters = new Dictionary<string, object?> { ["file_path"] = target, ["content"] = "written" };

        var denied = await executor.ExecuteAsync("write_file", parameters, Context());
        Assert.False(denied.IsSuccessful);

        Assert.True(mode.TrySet(AgentMode.Build, ModeChangeSource.UserCommand, out _));

        var allowed = await executor.ExecuteAsync("write_file", parameters, Context());
        Assert.True(allowed.IsSuccessful, allowed.ErrorMessage);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void PlanMode_AuthorizerReportsDeny_ForMutatingTool()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode);
        var store = sp.GetRequiredService<IPermissionStore>();
        store.AddSessionRule(PermissionRule.Parse("write_file(*)", PermissionOutcome.Allow, PermissionLayer.Session));

        var authorizer = sp.GetRequiredService<IToolPermissionAuthorizer>();
        var evaluation = authorizer.Evaluate(new ToolAuthorizationContext(
            "write_file",
            new Dictionary<string, object?> { ["file_path"] = Path.Combine(_workspace, "x.txt") },
            _workspace,
            null));

        Assert.Equal(PermissionOutcome.Deny, evaluation.Outcome);
    }

    [Fact]
    public void PlanMode_AuthorizerDelegates_ForReadOnlyTool()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode);

        var authorizer = sp.GetRequiredService<IToolPermissionAuthorizer>();
        var evaluation = authorizer.Evaluate(new ToolAuthorizationContext(
            "read_file",
            new Dictionary<string, object?> { ["file_path"] = Path.Combine(_workspace, "x.txt") },
            _workspace,
            null));

        Assert.Equal(PermissionOutcome.Allow, evaluation.Outcome);
    }

    [Fact]
    public async Task PlanMode_DeniesAnMcpToolThatWouldMutate()
    {
        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode, new AlwaysAllowPrompt());
        sp.GetRequiredService<IToolRegistry>()
            .RegisterTool(typeof(FakeMcpWriteTool), new Dictionary<string, object?>());

        // Grant the MCP tool the broadest possible permission at the highest layer the CLI installs.
        CliPermissionServiceExtensions.ApplyInjectedAllowList(sp, new[] { FakeMcpWriteTool.ToolId });
        sp.GetRequiredService<IPermissionStore>().AddSessionRule(
            PermissionRule.Parse($"{FakeMcpWriteTool.ToolId}(*)", PermissionOutcome.Allow, PermissionLayer.Session));

        var executor = sp.GetRequiredService<IToolExecutor>();
        var target = Path.Combine(_workspace, "mcp-write.txt");

        var result = await executor.ExecuteAsync(
            FakeMcpWriteTool.ToolId,
            new Dictionary<string, object?> { ["path"] = target },
            Context());

        Assert.False(result.IsSuccessful);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task BuildMode_RunsTheSameMcpTool()
    {
        // The companion to the test above: nothing about the fake tool is inherently blocked, so the
        // denial above is attributable to Plan mode and not to some unrelated wiring failure.
        var mode = new AgentModeState(AgentMode.Build);
        using var sp = BuildProvider(mode, new AlwaysAllowPrompt());
        sp.GetRequiredService<IToolRegistry>()
            .RegisterTool(typeof(FakeMcpWriteTool), new Dictionary<string, object?>());
        CliPermissionServiceExtensions.ApplyInjectedAllowList(sp, new[] { FakeMcpWriteTool.ToolId });

        var executor = sp.GetRequiredService<IToolExecutor>();
        var target = Path.Combine(_workspace, "mcp-write-build.txt");

        var result = await executor.ExecuteAsync(
            FakeMcpWriteTool.ToolId,
            new Dictionary<string, object?> { ["path"] = target },
            Context());

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task PlanMode_RunsAnMcpToolOnceItIsGrantedServerWide()
    {
        // The follow-up to the deny test above: an explicit opt-in is the ONLY thing that changes
        // the outcome, and it takes effect on the live provider without a restart.
        var mode = new AgentModeState(AgentMode.Plan);
        var grants = new PlanModeGrantStore(_workspace, _workspace);
        using var sp = BuildProvider(mode, new AlwaysAllowPrompt(), grants);
        sp.GetRequiredService<IToolRegistry>()
            .RegisterTool(typeof(FakeMcpReadTool), new Dictionary<string, object?>());
        CliPermissionServiceExtensions.ApplyInjectedAllowList(sp, new[] { FakeMcpReadTool.ToolId });

        var executor = sp.GetRequiredService<IToolExecutor>();
        var parameters = new Dictionary<string, object?>();

        var denied = await executor.ExecuteAsync(FakeMcpReadTool.ToolId, parameters, Context());
        Assert.False(denied.IsSuccessful);

        Assert.True(grants.GrantServer("fake").Success);

        var allowed = await executor.ExecuteAsync(FakeMcpReadTool.ToolId, parameters, Context());
        Assert.True(allowed.IsSuccessful, allowed.ErrorMessage);
    }

    [Fact]
    public async Task PlanMode_StillDeniesAMutatingMcpToolFromAServerWideGrantedServer()
    {
        // A server-wide grant is a Plan-mode READ-ONLY opt-in. It cannot rescue a call the policy
        // denies on capability grounds - here, one that names an output file.
        var mode = new AgentModeState(AgentMode.Plan);
        var grants = new PlanModeGrantStore(_workspace, _workspace);
        grants.GrantServer("fake");
        using var sp = BuildProvider(mode, new AlwaysAllowPrompt(), grants);
        sp.GetRequiredService<IToolRegistry>()
            .RegisterTool(typeof(FakeMcpWriteTool), new Dictionary<string, object?>());
        CliPermissionServiceExtensions.ApplyInjectedAllowList(sp, new[] { FakeMcpWriteTool.ToolId });

        var executor = sp.GetRequiredService<IToolExecutor>();
        var target = Path.Combine(_workspace, "granted-server-output.txt");

        var result = await executor.ExecuteAsync(
            FakeMcpWriteTool.ToolId,
            new Dictionary<string, object?> { ["output_file"] = target },
            Context());

        Assert.False(result.IsSuccessful);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task NonInteractiveHosts_ReadPersistedGrantsAndNeverPrompt()
    {
        // Headless / ACP / one-shot wire the same graph with interactiveBroker: null. The throwing
        // prompt proves nothing consults the user: the granted tool runs and the ungranted one is
        // denied purely from the persisted config.
        var grants = new PlanModeGrantStore(_workspace, _workspace);
        Assert.True(grants.GrantTools(new[] { FakeMcpReadTool.ToolId }).Success);

        var mode = new AgentModeState(AgentMode.Plan);
        using var sp = BuildProvider(mode, new ThrowingPrompt(), grants);
        var registry = sp.GetRequiredService<IToolRegistry>();
        registry.RegisterTool(typeof(FakeMcpReadTool), new Dictionary<string, object?>());
        registry.RegisterTool(typeof(FakeMcpWriteTool), new Dictionary<string, object?>());
        CliPermissionServiceExtensions.ApplyInjectedAllowList(
            sp, new[] { FakeMcpReadTool.ToolId, FakeMcpWriteTool.ToolId });

        var executor = sp.GetRequiredService<IToolExecutor>();

        var granted = await executor.ExecuteAsync(
            FakeMcpReadTool.ToolId, new Dictionary<string, object?>(), Context());
        Assert.True(granted.IsSuccessful, granted.ErrorMessage);

        var ungranted = await executor.ExecuteAsync(
            FakeMcpWriteTool.ToolId,
            new Dictionary<string, object?> { ["path"] = Path.Combine(_workspace, "nope.txt") },
            Context());
        Assert.False(ungranted.IsSuccessful);
    }

    [Fact]
    public void HeadlessServiceProvider_ResolvesTheGrantStoreWithoutAnyInteractivePrompt()
    {
        // Guards the composition itself: the headless graph must carry the mode overlay and its
        // grant store, and must NOT carry the interactive permission prompt.
        using var provider = Andy.Cli.Headless.HeadlessAgentRunner.BuildServiceProvider(
            MinimalHeadlessConfig(), NullLoggerFactory.Instance, AgentMode.Plan);

        Assert.NotNull(provider.GetService<PlanModeGrantStore>());
        Assert.NotNull(provider.GetService<ModeToolGate>());
        Assert.Equal(AgentMode.Plan, provider.GetRequiredService<AgentModeState>().Current);
        Assert.IsNotType<CliPermissionPrompt>(provider.GetService<IPermissionPrompt>());
    }

    /// <summary>A prompt that fails the test if anything tries to ask the user.</summary>
    private sealed class ThrowingPrompt : IPermissionPrompt
    {
        public Task<PermissionDecision> RequestAsync(
            PermissionRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                $"A non-interactive host must never prompt (tool: {request.ToolId}).");
    }

    /// <summary>A remote-looking MCP tool with no side effects, used to prove a grant takes effect.</summary>
    private sealed class FakeMcpReadTool : ITool
    {
        public const string ToolId = "mcp_fake_read_note";

        public ToolMetadata Metadata { get; } = new()
        {
            Id = ToolId,
            Name = ToolId,
            Description = "[MCP: fake] Reads a note.",
            Version = "1.0.0",
            Category = ToolCategory.Web,
        };

        public Task InitializeAsync(
            Dictionary<string, object?>? configuration = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IList<string> ValidateParameters(Dictionary<string, object?>? parameters)
            => Array.Empty<string>();

        public bool CanExecuteWithPermissions(ToolPermissions permissions) => true;

        public Task DisposeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ToolResult> ExecuteAsync(
            Dictionary<string, object?> parameters,
            ToolExecutionContext context)
            => Task.FromResult(ToolResult.Success(new { note = "read-only" }));
    }

    /// <summary>
    /// A stand-in for a remote MCP tool: like <c>McpRemoteTool</c> it declares no capabilities and
    /// no required permissions, so nothing in its metadata reveals that it mutates the workspace.
    /// That is precisely why Plan mode must fail closed on tools it cannot classify.
    /// </summary>
    private sealed class FakeMcpWriteTool : ITool
    {
        public const string ToolId = "mcp_fake_write_note";

        public ToolMetadata Metadata { get; } = new()
        {
            Id = ToolId,
            Name = ToolId,
            Description = "[MCP: fake] Writes a note.",
            Version = "1.0.0",
            Category = ToolCategory.Web,
        };

        public Task InitializeAsync(
            Dictionary<string, object?>? configuration = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IList<string> ValidateParameters(Dictionary<string, object?>? parameters)
            => Array.Empty<string>();

        public bool CanExecuteWithPermissions(ToolPermissions permissions) => true;

        public Task DisposeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ToolResult> ExecuteAsync(
            Dictionary<string, object?> parameters,
            ToolExecutionContext context)
        {
            var path = parameters.TryGetValue("path", out var raw) ? raw?.ToString() : null;
            if (!string.IsNullOrEmpty(path))
            {
                File.WriteAllText(path, "written by a remote tool");
            }

            return Task.FromResult(ToolResult.Success(new { written = path }));
        }
    }

    /// <summary>
    /// Stands in for a user who approves everything. Present to prove that Plan mode never even
    /// reaches the prompt: if it did, this prompt would allow the call and the test would fail.
    /// </summary>
    private sealed class AlwaysAllowPrompt : IPermissionPrompt
    {
        public Task<PermissionDecision> RequestAsync(
            PermissionRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PermissionDecision(true, PersistScope.Session));
    }
}
