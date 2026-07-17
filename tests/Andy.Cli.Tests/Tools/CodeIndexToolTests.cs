using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Tools;
using Andy.Tools.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Cli.Tests.Tools;

public class CodeIndexToolTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly CodeIndexTool _tool;
    private readonly IServiceProvider _serviceProvider;

    public CodeIndexToolTests()
    {
        // Create a temp directory for test files
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CodeIndexTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        // Create test C# files
        CreateTestFiles(_testDirectory);

        // Create a minimal service provider for the tool
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();

        _tool = new CodeIndexTool(_serviceProvider);

        // Initialize the tool
        _tool.InitializeAsync().GetAwaiter().GetResult();
    }

    private static void CreateTestFiles(string root)
    {
        // Create a simple test class
        var testClass = @"
namespace Andy.Cli.Services
{
    public class SimpleAssistantService
    {
        public async Task<string> ProcessMessageAsync(string message)
        {
            return message;
        }

        public void ClearContext()
        {
        }
    }
}";

        var servicesDir = Path.Combine(root, "Services");
        Directory.CreateDirectory(servicesDir);
        File.WriteAllText(Path.Combine(servicesDir, "SimpleAssistantService.cs"), testClass);

        // Create another test class
        var toolClass = @"
namespace Andy.Cli.Tools
{
    public class CodeIndexTool
    {
        public string Name { get; set; }

        public void Execute()
        {
        }
    }
}";

        var toolsDir = Path.Combine(root, "Tools");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(toolsDir, "CodeIndexTool.cs"), toolClass);

        // Create a widget class
        var widgetClass = @"
namespace Andy.Cli.Widgets
{
    public class FeedView
    {
        public void AddMarkdownRich(string content)
        {
        }
    }
}";

        var widgetsDir = Path.Combine(root, "Widgets");
        Directory.CreateDirectory(widgetsDir);
        File.WriteAllText(Path.Combine(widgetsDir, "FeedView.cs"), widgetClass);
    }

    private ToolExecutionContext Context(string? workingDir = null) => new()
    {
        WorkingDirectory = workingDir ?? _testDirectory
    };

    [Fact]
    public async Task SearchSymbols_FindsClassByName()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["query_type"] = "symbols",
            ["pattern"] = "SimpleAssistantService"
        };

        var result = await _tool.ExecuteAsync(parameters, Context());

        Assert.True(result.IsSuccessful, $"Tool execution failed: {result.Message}");
        var data = ExtractData(result);
        var count = Convert.ToInt32(data["count"]);
        Assert.True(count > 0, $"Expected to find SimpleAssistantService but found {count} symbols");

        var symbols = data["symbols"] as List<Dictionary<string, object?>>;
        Assert.NotNull(symbols);
        Assert.Contains(symbols!, s => s["name"]?.ToString() == "SimpleAssistantService");
    }

    [Fact]
    public async Task SearchSymbols_FindsClassWithScopeFilter()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["query_type"] = "symbols",
            ["pattern"] = "CodeIndexTool",
            ["scope"] = "Tools"
        };

        var result = await _tool.ExecuteAsync(parameters, Context());

        Assert.True(result.IsSuccessful);
        var data = ExtractData(result);
        var count = Convert.ToInt32(data["count"]);
        Assert.True(count > 0, $"Expected to find CodeIndexTool in Tools scope but found {count} symbols");
    }

    [Fact]
    public async Task SearchSymbols_FindsMultipleClassesWithWildcard()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["query_type"] = "symbols",
            ["pattern"] = "*"
        };

        var result = await _tool.ExecuteAsync(parameters, Context());

        Assert.True(result.IsSuccessful);
        var data = ExtractData(result);
        var count = Convert.ToInt32(data["count"]);
        Assert.True(count >= 3, $"Expected to find at least 3 symbols but found {count}");

        var symbols = data["symbols"] as List<Dictionary<string, object?>>;
        var classNames = symbols!.Where(s => s["kind"]?.ToString() == "class")
                                 .Select(s => s["name"]?.ToString())
                                 .ToList();

        Assert.Contains("SimpleAssistantService", classNames);
        Assert.Contains("CodeIndexTool", classNames);
        Assert.Contains("FeedView", classNames);
    }

    [Fact]
    public async Task SearchSymbols_FindsMethods()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["query_type"] = "symbols",
            ["pattern"] = "ProcessMessageAsync"
        };

        var result = await _tool.ExecuteAsync(parameters, Context());

        Assert.True(result.IsSuccessful);
        var data = ExtractData(result);
        var count = Convert.ToInt32(data["count"]);
        Assert.True(count > 0, "Expected to find ProcessMessageAsync method");

        var symbols = data["symbols"] as List<Dictionary<string, object?>>;
        Assert.Contains(symbols!, s =>
            s["name"]?.ToString() == "ProcessMessageAsync" &&
            s["kind"]?.ToString() == "method");
    }

    [Fact]
    public async Task GetProjectStructure_ReturnsNamespaces()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["query_type"] = "structure"
        };

        var result = await _tool.ExecuteAsync(parameters, Context());

        Assert.True(result.IsSuccessful);
        var data = ExtractData(result);
        Assert.True(data.ContainsKey("namespaces"));
        Assert.True(data.ContainsKey("namespace_count"));
        Assert.True(data.ContainsKey("file_count"));
        Assert.True(data.ContainsKey("directories"));
    }

    // #175: The tool must index the workspace named by context.WorkingDirectory, not the process
    // working directory. Point the same tool at two distinct temp workspaces and confirm each query
    // sees only the symbols of the workspace it was pointed at.
    [Fact]
    public async Task Index_UsesContextWorkingDirectory_NotProcessCwd()
    {
        var workspaceA = Path.Combine(Path.GetTempPath(), $"CodeIndexWsA_{Guid.NewGuid():N}");
        var workspaceB = Path.Combine(Path.GetTempPath(), $"CodeIndexWsB_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);
        try
        {
            File.WriteAllText(Path.Combine(workspaceA, "OnlyInA.cs"),
                "namespace Ws.A { public class OnlyInAClass { } }");
            File.WriteAllText(Path.Combine(workspaceB, "OnlyInB.cs"),
                "namespace Ws.B { public class OnlyInBClass { } }");

            var tool = new CodeIndexTool(_serviceProvider);
            await tool.InitializeAsync();

            // Query workspace A.
            var resultA = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["query_type"] = "symbols", ["pattern"] = "*" },
                Context(workspaceA));
            var namesA = ClassNames(resultA);
            Assert.Contains("OnlyInAClass", namesA);
            Assert.DoesNotContain("OnlyInBClass", namesA);

            // Query workspace B with the SAME tool instance: it must re-index to B.
            var resultB = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["query_type"] = "symbols", ["pattern"] = "*" },
                Context(workspaceB));
            var namesB = ClassNames(resultB);
            Assert.Contains("OnlyInBClass", namesB);
            Assert.DoesNotContain("OnlyInAClass", namesB);
        }
        finally
        {
            TryDelete(workspaceA);
            TryDelete(workspaceB);
        }
    }

    // #175: A malformed wildcard/pattern must not crash regex construction; the tool returns a
    // successful (possibly empty) result rather than throwing.
    [Theory]
    [InlineData("[")]
    [InlineData("(")]
    [InlineData("*(unclosed")]
    [InlineData("a{2,")]
    [InlineData("\\")]
    public async Task SearchSymbols_InvalidPattern_DoesNotThrow(string pattern)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["query_type"] = "symbols",
            ["pattern"] = pattern
        };

        var result = await _tool.ExecuteAsync(parameters, Context());

        // Must not throw and must not fail hard - the query completes.
        Assert.True(result.IsSuccessful, $"Pattern '{pattern}' should be handled gracefully: {result.Message}");
        var data = ExtractData(result);
        Assert.True(data.ContainsKey("count"));
    }

    // #175: hierarchy must return real relationships (base types + derived types), not a placeholder.
    [Fact]
    public async Task Hierarchy_ReturnsBaseTypesAndDerivedTypes()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"CodeIndexHier_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "Types.cs"), @"
namespace Hier
{
    public interface IShape { }
    public class Shape { }
    public class Circle : Shape, IShape { }
}");

            var tool = new CodeIndexTool(_serviceProvider);
            await tool.InitializeAsync();

            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["query_type"] = "hierarchy", ["pattern"] = "Circle" },
                Context(workspace));

            Assert.True(result.IsSuccessful, result.Message);
            var data = ExtractData(result);
            Assert.Equal(true, data["found"]);
            var baseClasses = (data["baseClasses"] as IEnumerable<string>)!.ToList();
            var interfaces = (data["interfaces"] as IEnumerable<string>)!.ToList();
            Assert.Contains("Shape", baseClasses);
            Assert.Contains("IShape", interfaces);

            // Shape should report Circle as a derived class.
            var shapeResult = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["query_type"] = "hierarchy", ["pattern"] = "Shape" },
                Context(workspace));
            var shapeData = ExtractData(shapeResult);
            var derived = (shapeData["derivedClasses"] as IEnumerable<string>)!.ToList();
            Assert.Contains("Circle", derived);
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    // #175: the tool no longer advertises the placeholder 'references' operation.
    [Fact]
    public void Schema_DoesNotAdvertiseReferences()
    {
        var queryType = _tool.Metadata.Parameters.First(p => p.Name == "query_type");
        Assert.NotNull(queryType.AllowedValues);
        Assert.DoesNotContain("references", queryType.AllowedValues!.Select(v => v?.ToString()));
        Assert.Contains("hierarchy", queryType.AllowedValues!.Select(v => v?.ToString()));
    }

    // #175: cancellation is honored - a pre-cancelled token yields a graceful failure, not a hang or
    // an unhandled exception.
    [Fact]
    public async Task Execute_HonorsCancellation()
    {
        var tool = new CodeIndexTool(_serviceProvider);
        await tool.InitializeAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = new ToolExecutionContext
        {
            WorkingDirectory = _testDirectory,
            CancellationToken = cts.Token
        };

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["query_type"] = "symbols", ["pattern"] = "*" },
            context);

        Assert.False(result.IsSuccessful);
    }

    // #175: allowed-path policy is enforced - a working directory outside the allow-list is rejected.
    [Fact]
    public async Task Execute_RejectsWorkspaceOutsideAllowedPaths()
    {
        var allowed = Path.Combine(Path.GetTempPath(), $"CodeIndexAllowed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(allowed);
        try
        {
            var context = new ToolExecutionContext
            {
                WorkingDirectory = _testDirectory,
                Permissions = new ToolPermissions { AllowedPaths = new HashSet<string> { allowed } }
            };

            var result = await _tool.ExecuteAsync(
                new Dictionary<string, object?> { ["query_type"] = "symbols", ["pattern"] = "*" },
                context);

            Assert.False(result.IsSuccessful);
            var message = $"{result.ErrorMessage} {result.Message}";
            Assert.Contains("allowed paths", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(allowed);
        }
    }

    private static Dictionary<string, object?> ExtractData(ToolResult result)
    {
        var resultDict = result.Data as Dictionary<string, object?>;
        Assert.NotNull(resultDict);
        var data = resultDict!["data"] as Dictionary<string, object?>;
        Assert.NotNull(data);
        return data!;
    }

    private static List<string?> ClassNames(ToolResult result)
    {
        var data = ExtractData(result);
        var symbols = data["symbols"] as List<Dictionary<string, object?>>;
        return symbols!.Where(s => s["kind"]?.ToString() == "class")
                       .Select(s => s["name"]?.ToString())
                       .ToList();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        TryDelete(_testDirectory);
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
