using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// End-to-end check for issue #134: a model calls replace_text with old_string/new_string/file_path
/// (the names from its own edit tools). After ParameterMapper.MapAndNormalize maps them to the
/// tool's real parameters, the real ReplaceTextTool must actually perform the replacement.
/// </summary>
public sealed class ReplaceTextParameterIntegrationTests
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
        services.AddSingleton(new ToolFrameworkOptions
        {
            RegisterBuiltInTools = false,
            EnableObservability = false,
            AutoDiscoverTools = false,
        });
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
            registry.RegisterTool(reg.ToolType, reg.Configuration);
        return sp;
    }

    [Fact]
    public async Task ReplaceText_WithOldNewStringAliases_ActuallyReplaces()
    {
        using var sp = BuildProvider();
        var exec = sp.GetRequiredService<IToolExecutor>();
        var registry = sp.GetRequiredService<IToolRegistry>();
        var meta = registry.GetTools().First(t => t.Metadata.Id == "replace_text").Metadata;

        var dir = Path.Combine(Path.GetTempPath(), "andy_replace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "greeting.txt");
        await File.WriteAllTextAsync(file, "hello world");

        try
        {
            // The model's call, using its own edit-tool parameter names.
            var modelArgs = new Dictionary<string, object?>
            {
                ["old_string"] = "world",
                ["new_string"] = "andy",
                ["file_path"] = file,
                ["create_backup"] = false,
            };

            var mapped = ParameterMapper.MapAndNormalize("replace_text", modelArgs, meta);
            // The required parameters must now be present (this is what was null before #134).
            Assert.Equal("world", mapped["search_pattern"]);
            Assert.Equal("andy", mapped["replacement_text"]);

            var ctx = new ToolExecutionContext
            {
                Permissions = new ToolPermissions { FileSystemAccess = true },
            };
            var result = await exec.ExecuteAsync("replace_text", mapped, ctx);

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            var contents = await File.ReadAllTextAsync(file);
            Assert.Equal("hello andy", contents);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
