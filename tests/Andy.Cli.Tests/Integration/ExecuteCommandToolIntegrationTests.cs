using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Permissions.DependencyInjection;
using Andy.Tools.Core;
using Andy.Tools.Core.OutputLimiting;
using Andy.Tools.Discovery;
using Andy.Tools.Execution;
using Andy.Tools.Framework;
using Andy.Tools.Registry;
using Andy.Tools.Validation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// Integration tests that run the real <c>execute_command</c> tool (the andy-cli bash tool, provided by
/// Andy.Tools) through the same permission-gated <c>ToolExecutor</c> the CLI wires up. The store is
/// isolated (no user/project files) so results are deterministic on any machine. Env-mutating tests run
/// in a single non-parallel collection.
/// </summary>
[CollectionDefinition("bash-tool-env", DisableParallelization = true)]
public sealed class BashToolEnvCollection { }

[Collection("bash-tool-env")]
public sealed class ExecuteCommandToolIntegrationTests
{
    /// <summary>Builds an andy-cli-representative provider with an isolated permission store.</summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Core Andy.Tools services — mirrors Program.cs.
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

        // andy-cli's tool catalog (registers execute_command, the bash tool).
        ToolCatalog.RegisterAllTools(services);

        // The permission engine the CLI uses; isolate file layers for deterministic tests.
        services.AddAndyPermissions(o =>
        {
            o.UserFilePath = null;
            o.ProjectFilePath = null;
            o.LocalFilePath = null;
            o.ManagedFilePath = null;
        });

        var sp = services.BuildServiceProvider();

        // Populate the registry from the catalog markers — mirrors Program.cs.
        var registry = sp.GetRequiredService<IToolRegistry>();
        foreach (var reg in sp.GetServices<ToolRegistrationInfo>())
        {
            registry.RegisterTool(reg.ToolType, reg.Configuration);
        }

        return sp;
    }

    private static ToolExecutionContext Context() => new()
    {
        Permissions = new ToolPermissions { ProcessExecution = true },
    };

    private static Dictionary<string, object?> Cmd(string command) => new() { ["command"] = command };

    private static (bool Ok, int ExitCode, string Stdout, string? Error) Read(ToolExecutionResult result)
    {
        var data = result.Data as Dictionary<string, object?>;
        var exit = data is not null && data.TryGetValue("exit_code", out var e) && e is int i ? i : -999;
        var stdout = data is not null && data.TryGetValue("stdout", out var s) ? s?.ToString() ?? "" : "";
        return (result.IsSuccessful, exit, stdout, result.ErrorMessage);
    }

    [Fact]
    public async Task Echo_runs_and_returns_output()
    {
        using var sp = BuildProvider();
        var exec = sp.GetRequiredService<IToolExecutor>();

        // echo is a known-safe read-only command, so it is auto-allowed (no prompt).
        var result = await exec.ExecuteAsync("execute_command", Cmd("echo hello_andy_cli"), Context());

        var (ok, exit, stdout, err) = Read(result);
        Assert.True(ok, err);
        Assert.Equal(0, exit);
        Assert.Contains("hello_andy_cli", stdout);
    }

    [Fact]
    public async Task Execute_command_runs_with_engine_default_profile_via_ui_executor()
    {
        // Reproduces the live path: the engine's SimpleAgent builds the context with the restrictive
        // DEFAULT permission profile (ProcessExecution = false) and the call goes through
        // UiUpdatingToolExecutor. execute_command declares the ProcessExecution capability, so without the
        // capability grant in UiUpdatingToolExecutor it would be blocked before the permission gate runs.
        using var sp = BuildProvider();
        var inner = sp.GetRequiredService<IToolExecutor>();
        var exec = new UiUpdatingToolExecutor(inner);

        var defaultContext = new ToolExecutionContext(); // ProcessExecution defaults to false, like SimpleAgent
        var result = await exec.ExecuteAsync("execute_command", Cmd("echo via_ui_executor"), defaultContext);

        var (ok, _, stdout, err) = Read(result);
        Assert.True(ok, err);
        Assert.Contains("via_ui_executor", stdout);
    }

    [Fact]
    public async Task Known_safe_command_pwd_or_cd_runs()
    {
        using var sp = BuildProvider();
        var exec = sp.GetRequiredService<IToolExecutor>();

        // "echo" works on both bash and cmd; assert the tool plumbs stdout through.
        var result = await exec.ExecuteAsync("execute_command", Cmd("echo one && echo two"), Context());

        // 'echo one && echo two' decomposes to two echo segments — both known-safe ⇒ allowed.
        var (ok, _, stdout, err) = Read(result);
        Assert.True(ok, err);
        Assert.Contains("one", stdout);
        Assert.Contains("two", stdout);
    }

    [Fact]
    public async Task Nonzero_exit_is_surfaced_as_failure()
    {
        using var sp = BuildProvider();
        var exec = sp.GetRequiredService<IToolExecutor>();

        // 'false' is known-safe and returns a non-zero exit code.
        var result = await exec.ExecuteAsync("execute_command", Cmd("false"), Context());

        var (ok, exit, _, _) = Read(result);
        Assert.False(ok);            // non-zero exit ⇒ tool reports failure
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task Neutral_command_is_blocked_without_a_rule_then_allowed_when_injected()
    {
        // No rule + non-interactive (fail-closed) ⇒ a neutral command is denied by the permission gate.
        using (var sp = BuildProvider())
        {
            var exec = sp.GetRequiredService<IToolExecutor>();
            var result = await exec.ExecuteAsync("execute_command", Cmd("dotnet --version"), Context());
            Assert.False(result.IsSuccessful);
            Assert.Contains("permission", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // Inject an allow rule (container-style) ⇒ the same command now runs with no prompt.
        var prev = Environment.GetEnvironmentVariable(PermissionInjectionBootstrap.JsonEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PermissionInjectionBootstrap.JsonEnvVar,
                """{ "allow": ["execute_command(*)"] }""");

            using var sp = BuildProvider();
            var exec = sp.GetRequiredService<IToolExecutor>();
            var result = await exec.ExecuteAsync("execute_command", Cmd("dotnet --version"), Context());

            var (ok, exit, stdout, err) = Read(result);
            Assert.True(ok, err);
            Assert.Equal(0, exit);
            Assert.Matches(@"\d+\.\d+", stdout);   // a version number
        }
        finally
        {
            Environment.SetEnvironmentVariable(PermissionInjectionBootstrap.JsonEnvVar, prev);
        }
    }

    [Fact]
    public async Task Dangerous_command_is_blocked_by_builtin_deny()
    {
        using var sp = BuildProvider();
        var exec = sp.GetRequiredService<IToolExecutor>();

        // Builtin deny for "rm -rf /" — gate blocks it before the tool ever runs.
        var result = await exec.ExecuteAsync("execute_command", Cmd("rm -rf /"), Context());

        Assert.False(result.IsSuccessful);
        Assert.Contains("permission", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_process_execution_permission_is_denied()
    {
        using var sp = BuildProvider();
        var exec = sp.GetRequiredService<IToolExecutor>();

        var ctx = new ToolExecutionContext { Permissions = new ToolPermissions { ProcessExecution = false } };
        var result = await exec.ExecuteAsync("execute_command", Cmd("echo hi"), ctx);

        Assert.False(result.IsSuccessful);
    }
}
