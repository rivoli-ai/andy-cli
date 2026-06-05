using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
/// Integration tests for the <c>list_directory</c> tool running through the same permission-gated
/// <c>ToolExecutor</c> andy-cli wires up (isolated permission store for deterministic results). Shares the
/// non-parallel "bash-tool-env" collection because one case mutates process environment.
/// </summary>
[Collection("bash-tool-env")]
public sealed class ListDirectoryIntegrationTests
{
    private static ServiceProvider BuildProvider()
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
        services.AddSingleton(new ToolFrameworkOptions { RegisterBuiltInTools = false, EnableObservability = false, AutoDiscoverTools = false });
        ToolCatalog.RegisterAllTools(services);
        services.AddAndyPermissions(o =>
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

    private static ToolExecutionContext Context() => new()
    {
        Permissions = new ToolPermissions { FileSystemAccess = true },
    };

    [Fact]
    public async Task List_directory_returns_entries_and_auto_allows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "andy-ld-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker_file.txt"), "x");
        try
        {
            using var sp = BuildProvider();
            var exec = sp.GetRequiredService<IToolExecutor>();

            // list_directory is read-only with no rule ⇒ auto-allowed (no prompt).
            var result = await exec.ExecuteAsync("list_directory",
                new Dictionary<string, object?> { ["directory_path"] = dir }, Context());

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            var json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("marker_file.txt", json);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task List_directory_is_blocked_by_an_injected_deny_rule()
    {
        var prev = Environment.GetEnvironmentVariable(PermissionInjectionBootstrap.JsonEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PermissionInjectionBootstrap.JsonEnvVar,
                """{ "deny": ["list_directory(**)"] }""");

            using var sp = BuildProvider();
            var exec = sp.GetRequiredService<IToolExecutor>();

            var result = await exec.ExecuteAsync("list_directory",
                new Dictionary<string, object?> { ["directory_path"] = Path.GetTempPath() }, Context());

            Assert.False(result.IsSuccessful);
            Assert.Contains("permission", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PermissionInjectionBootstrap.JsonEnvVar, prev);
        }
    }
}
