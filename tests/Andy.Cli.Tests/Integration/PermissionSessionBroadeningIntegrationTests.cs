using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
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
/// End-to-end tests for the over-prompting fix, exercising the real CLI wiring
/// (<see cref="CliPermissionServiceExtensions.AddAndyCliPermissions"/> -> interactive
/// <see cref="CliPermissionPrompt"/> -> permission engine -> real <c>execute_command</c> tool). They prove
/// that after one session "Allow" the user is NOT re-prompted for a similar command (same executable +
/// subcommand, different args), but IS still prompted for a genuinely different command.
/// </summary>
[Collection("bash-tool-env")]
public sealed class PermissionSessionBroadeningIntegrationTests
{
    /// <summary>Builds the CLI's real permission-gated executor wiring with an interactive broker.</summary>
    private static ServiceProvider BuildProvider(PermissionRequestBroker broker)
    {
        var services = new ServiceCollection();
        services.AddLogging();

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

        // The exact wiring Program.cs uses (interactive prompt + broadening), with isolated file layers
        // so the result is deterministic regardless of any user/project permission files on the machine.
        services.AddAndyCliPermissions(broker, o =>
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

    private static Dictionary<string, object?> Cmd(string command) => new() { ["command"] = command };

    /// <summary>
    /// Drains the broker on a background loop, answering every prompt with the given decision and counting
    /// how many prompts were shown, until cancelled.
    /// </summary>
    private sealed class BrokerDriver : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        public int PromptCount;

        public BrokerDriver(PermissionRequestBroker broker, Andy.Permissions.Model.PermissionDecision decision)
        {
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (broker.TryDequeue(out var pending) && pending != null)
                    {
                        Interlocked.Increment(ref PromptCount);
                        pending.Completion.TrySetResult(decision);
                    }
                    else
                    {
                        await Task.Delay(5);
                    }
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop.Wait(2000); } catch { /* best-effort shutdown */ }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Allow_session_for_a_command_does_not_reprompt_for_a_similar_command()
    {
        var broker = new PermissionRequestBroker();
        var allowSession = new Andy.Permissions.Model.PermissionDecision(
            true, Andy.Permissions.Model.PersistScope.Session);
        using var driver = new BrokerDriver(broker, allowSession);
        using var sp = BuildProvider(broker);
        var exec = new UiUpdatingToolExecutor(sp.GetRequiredService<IToolExecutor>());

        // 'dotnet' is neutral (not on the engine's known-safe list) so the gate asks the first time.
        var first = await exec.ExecuteAsync("execute_command", Cmd("dotnet help"), new ToolExecutionContext());
        Assert.True(first.IsSuccessful, first.ErrorMessage);
        Assert.Equal(1, driver.PromptCount);

        // Different args, SAME command class (dotnet help): broadened session rule covers it -> no new prompt.
        var second = await exec.ExecuteAsync("execute_command", Cmd("dotnet help build"), new ToolExecutionContext());
        Assert.True(second.IsSuccessful, second.ErrorMessage);
        Assert.Equal(1, driver.PromptCount);
    }

    [Fact]
    public async Task Allow_session_does_not_authorize_a_genuinely_different_command()
    {
        var broker = new PermissionRequestBroker();
        var allowSession = new Andy.Permissions.Model.PermissionDecision(
            true, Andy.Permissions.Model.PersistScope.Session);
        using var driver = new BrokerDriver(broker, allowSession);
        using var sp = BuildProvider(broker);
        var exec = new UiUpdatingToolExecutor(sp.GetRequiredService<IToolExecutor>());

        await exec.ExecuteAsync("execute_command", Cmd("dotnet help"), new ToolExecutionContext());
        Assert.Equal(1, driver.PromptCount);

        // A different command class ('dotnet' with a flag, not the 'dotnet help' subcommand) is NOT covered
        // by the 'dotnet help' grant -> a new prompt is shown.
        await exec.ExecuteAsync("execute_command", Cmd("dotnet --info"), new ToolExecutionContext());
        Assert.Equal(2, driver.PromptCount);
    }
}
