// AX.3 (rivoli-ai/conductor#2090): headless mode must expose the assistant's
// built-in tools (read/write/edit files, run command, list dir, ...) in the
// SAME IToolRegistry the SimpleAgent receives — not just the config-declared
// cli/mcp tools. Before AX.3 the headless DI registered NO built-in tools.
//
// These tests exercise the REAL production wiring (BuildServiceProvider +
// RegisterBuiltInTools, both internal) so they would FAIL against the old code
// (registry empty of built-ins) and PASS now.

using System.Linq;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Tools.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class HeadlessBuiltInToolsTests
{
    private static HeadlessRunConfig MinimalConfig() => new()
    {
        SchemaVersion = 1,
        RunId = Guid.NewGuid(),
        Agent = new HeadlessAgent { Slug = "ax3-agent", Instructions = "stub" },
        Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
        Tools = Array.Empty<HeadlessTool>(),
        Workspace = new HeadlessWorkspace { Root = System.IO.Path.GetTempPath() },
        Output = new HeadlessOutput { File = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ax3-out.txt"), Stream = "stdout" },
        Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 30 },
    };

    private static IToolRegistry BuildRegistryWithBuiltIns()
    {
        var services = HeadlessAgentRunner.BuildServiceProvider(MinimalConfig(), NullLoggerFactory.Instance);
        HeadlessAgentRunner.RegisterBuiltInTools(services, NullLoggerFactory.Instance);
        return services.GetRequiredService<IToolRegistry>();
    }

    [Fact]
    public void Headless_RegistersCoreFileTools()
    {
        var registry = BuildRegistryWithBuiltIns();
        var ids = registry.Tools.Select(t => t.Metadata.Id).ToHashSet(StringComparer.Ordinal);

        // Read-only and mutating file tools are both present (visibility is the
        // AX.3 concern; AX.4 governs whether mutating ones are *permitted*).
        Assert.Contains("read_file", ids);
        Assert.Contains("write_file", ids);
        Assert.Contains("list_directory", ids);
    }

    [Fact]
    public void Headless_RegistersCommandExecutionTool()
    {
        var registry = BuildRegistryWithBuiltIns();
        var ids = registry.Tools.Select(t => t.Metadata.Id).ToHashSet(StringComparer.Ordinal);

        // The "run a command" tool the coding agent needs.
        Assert.Contains("execute_command", ids);
    }

    [Fact]
    public void Headless_BuiltInTools_AreResolvableFromTheRegistry()
    {
        var registry = BuildRegistryWithBuiltIns();

        // GetTool is the lookup SimpleAgent uses; prove a built-in resolves.
        Assert.NotNull(registry.GetTool("read_file"));
        Assert.NotNull(registry.GetTool("execute_command"));
    }

    [Fact]
    public async Task Headless_BuiltInTools_AndConfigTools_Coexist_InSameRegistry()
    {
        // A config-declared cli tool added by HeadlessToolHost must land in the
        // same registry as the built-ins (no clobbering).
        var config = MinimalConfig();
        var cliTool = new HeadlessTool
        {
            Name = "my_cli_tool",
            Transport = "cli",
            Command = OperatingSystem.IsWindows()
                ? new[] { "cmd", "/c", "echo" }
                : new[] { "echo" },
        };

        var services = HeadlessAgentRunner.BuildServiceProvider(config, NullLoggerFactory.Instance);
        HeadlessAgentRunner.RegisterBuiltInTools(services, NullLoggerFactory.Instance);
        var registry = services.GetRequiredService<IToolRegistry>();

        // Mirror HeadlessToolHost: register the cli adapter into the same registry.
        await using var host = await HeadlessToolHost
            .BuildAsync(new[] { cliTool }, registry, NullLoggerFactory.Instance);

        var ids = registry.Tools.Select(t => t.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("read_file", ids);          // built-in survives
        Assert.Contains("my_cli_tool", ids);        // config tool present
    }
}
