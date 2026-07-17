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
    private static ServiceProvider BuildProvider(Andy.Permissions.Prompt.IPermissionPrompt? prompt = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (prompt is not null)
        {
            services.AddSingleton(prompt); // registered before AddAndyPermissions (which TryAdds a default)
        }

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
    public async Task Ask_then_allow_session_via_prompt_runs_command_and_remembers()
    {
        // Mirrors the live interactive flow the user hit: a neutral command (not known-safe) ⇒ the gate
        // asks; the user picks "Allow (session)" in the modal; the command must then ACTUALLY run (the
        // capability grant in UiUpdatingToolExecutor lets ToolBase/SecurityManager pass) and must not
        // prompt again for the same command this session.
        var prompt = new AllowSessionPrompt();
        using var sp = BuildProvider(prompt);
        var exec = new UiUpdatingToolExecutor(sp.GetRequiredService<IToolExecutor>());

        // Root cause: session-scoped consent is remembered in a PROCESS-STATIC store that outlives the
        // per-test DI provider and is never reset between tests. If this test used the same command as
        // Neutral_command_is_blocked_without_a_rule_then_allowed_when_injected ("dotnet --version"), the
        // session grant established here would leak into that test and make its "blocked without a rule"
        // assertion pass or fail depending on test execution order (#170).
        //
        // The proper fix is a harness change that resets the process-static session-consent store
        // between tests; until that lands, this workaround simply uses a DISTINCT neutral command
        // ("dotnet --info") so the two tests cannot cross-contaminate through the shared static store.
        var first = await exec.ExecuteAsync("execute_command", Cmd("dotnet --info"), new ToolExecutionContext());
        var (ok1, _, stdout1, err1) = Read(first);
        Assert.True(ok1, err1);                 // ran after the user allowed (this is what was failing live)
        Assert.Matches(@"\d+\.\d+", stdout1);
        Assert.Equal(1, prompt.CallCount);      // prompted exactly once

        var second = await exec.ExecuteAsync("execute_command", Cmd("dotnet --info"), new ToolExecutionContext());
        Assert.True(second.IsSuccessful, second.ErrorMessage);
        Assert.Equal(1, prompt.CallCount);      // "session" remembered ⇒ no second prompt
    }

    private sealed class AllowSessionPrompt : Andy.Permissions.Prompt.IPermissionPrompt
    {
        public int CallCount;
        public Task<Andy.Permissions.Model.PermissionDecision> RequestAsync(
            Andy.Permissions.Model.PermissionRequest request, System.Threading.CancellationToken cancellationToken = default)
        {
            System.Threading.Interlocked.Increment(ref CallCount);
            return Task.FromResult(new Andy.Permissions.Model.PermissionDecision(true, Andy.Permissions.Model.PersistScope.Session));
        }
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
    public async Task Working_directory_parameter_runs_command_in_dir_without_cd_preamble()
    {
        // Proves the fix: a different run directory is selected via the tool's working_directory
        // parameter (which sets ProcessStartInfo.WorkingDirectory) rather than a "cd <dir> &&" preamble
        // on the command. The executed command stays clean, so the approval preview matches what runs.
        using var sp = BuildProvider();
        var exec = sp.GetRequiredService<IToolExecutor>();

        var tempDir = System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar);

        // "pwd" is known-safe; no "cd" appears anywhere in the command string.
        var parameters = new Dictionary<string, object?>
        {
            ["command"] = "pwd",
            ["working_directory"] = tempDir,
        };
        var result = await exec.ExecuteAsync("execute_command", parameters, Context());

        var (ok, exit, stdout, err) = Read(result);
        Assert.True(ok, err);
        Assert.Equal(0, exit);
        // The command ran in the requested directory even though it contained no "cd".
        Assert.Contains(System.IO.Path.GetFileName(tempDir), stdout);
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
