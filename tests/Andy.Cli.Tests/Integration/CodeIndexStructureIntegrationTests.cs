using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Integration check for the code_index tool's "structure" query. Regression guard for the bug
/// where the structure output repeated every file's absolute path several times (~60KB for a
/// mid-size repo), overflowed the model's tool-output budget and got truncated - so "what is the
/// structure of this repo" returned nothing usable. The output must now be compact and useful:
/// counts, a directory rollup, and namespace/class names, with no repeated absolute paths.
/// </summary>
public sealed class CodeIndexStructureIntegrationTests
{
    private static ServiceProvider BuildProvider(CodeIndexingService indexed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(indexed);
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

    private static string CreateSampleRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ci_struct_" + Guid.NewGuid().ToString("N"));
        for (int n = 0; n < 6; n++)
        {
            var nsDir = Path.Combine(dir, $"Ns{n}");
            Directory.CreateDirectory(nsDir);
            for (int f = 0; f < 12; f++)
            {
                File.WriteAllText(Path.Combine(nsDir, $"Class{n}_{f}.cs"),
                    $"namespace Demo.Ns{n} {{ public class Class{n}_{f} {{ public void M(){{}} }} " +
                    $"public interface IThing{n}_{f} {{ void Do(); }} }}");
            }
        }
        return dir;
    }

    [Fact]
    public async Task Structure_IsCompactAndUsable_NotTruncatedAbsolutePathDump()
    {
        var dir = CreateSampleRepo();
        try
        {
            var indexer = new CodeIndexingService();
            await indexer.IndexDirectoryAsync(dir);

            using var sp = BuildProvider(indexer);
            var exec = sp.GetRequiredService<IToolExecutor>();

            var result = await exec.ExecuteAsync("code_index",
                new Dictionary<string, object?> { ["query_type"] = "structure" },
                new ToolExecutionContext { WorkingDirectory = dir, Permissions = new ToolPermissions { FileSystemAccess = true } });

            Assert.True(result.IsSuccessful, result.ErrorMessage);

            var json = JsonSerializer.Serialize(result.Data);

            // Real structure content is present and usable.
            Assert.Contains("Demo.Ns0", json);          // namespace names
            Assert.Contains("Class0_0", json);           // class names
            Assert.Contains("namespace_count", json);    // counts the model can read
            Assert.Contains("directories", json);        // the directory-level rollup

            // The pathological bloat is gone: 72 files used to serialize to ~22KB by repeating
            // absolute paths. Compact output is a fraction of that and never repeats the temp
            // root's absolute path dozens of times.
            Assert.True(json.Length < 8000, $"structure output should be compact, was {json.Length} chars");
            int absPathOccurrences = json.Split(new[] { dir }, StringSplitOptions.None).Length - 1;
            Assert.True(absPathOccurrences <= 1,
                $"absolute root path should appear at most once, appeared {absPathOccurrences} times");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
