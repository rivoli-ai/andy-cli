using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        CreateTestFiles();

        // Create a minimal service provider for the tool
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();

        _tool = new CodeIndexTool(_serviceProvider);

        // Initialize the tool
        _tool.InitializeAsync().GetAwaiter().GetResult();
    }

    private void CreateTestFiles()
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

        var servicesDir = Path.Combine(_testDirectory, "Services");
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

        var toolsDir = Path.Combine(_testDirectory, "Tools");
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

        var widgetsDir = Path.Combine(_testDirectory, "Widgets");
        Directory.CreateDirectory(widgetsDir);
        File.WriteAllText(Path.Combine(widgetsDir, "FeedView.cs"), widgetClass);
    }

    [Fact]
    public async Task SearchSymbols_FindsClassByName()
    {
        // Arrange
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["query_type"] = "symbols",
                ["pattern"] = "SimpleAssistantService"
            };

            var context = new ToolExecutionContext();

            // Act
            var result = await _tool.ExecuteAsync(parameters, context);

            // Assert
            Assert.True(result.IsSuccessful, $"Tool execution failed: {result.Message}");
            Assert.NotNull(result.Data);

            var resultDict = result.Data as Dictionary<string, object?>;
            Assert.NotNull(resultDict);

            var data = resultDict["data"] as Dictionary<string, object?>;
            Assert.NotNull(data);

            var count = Convert.ToInt32(data["count"]);
            Assert.True(count > 0, $"Expected to find SimpleAssistantService but found {count} symbols");

            var symbols = data["symbols"] as List<Dictionary<string, object?>>;
            Assert.NotNull(symbols);
            Assert.Contains(symbols, s => s["name"]?.ToString() == "SimpleAssistantService");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    [Fact]
    public async Task SearchSymbols_FindsClassWithScopeFilter()
    {
        // Arrange
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["query_type"] = "symbols",
                ["pattern"] = "CodeIndexTool",
                ["scope"] = "Tools"
            };

            var context = new ToolExecutionContext();

            // Act
            var result = await _tool.ExecuteAsync(parameters, context);

            // Assert
            Assert.True(result.IsSuccessful);
            var resultDict = result.Data as Dictionary<string, object?>;
            var data = resultDict!["data"] as Dictionary<string, object?>;
            var count = Convert.ToInt32(data!["count"]);

            Assert.True(count > 0, $"Expected to find CodeIndexTool in Tools scope but found {count} symbols");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    [Fact]
    public async Task SearchSymbols_FindsMultipleClassesWithWildcard()
    {
        // Arrange
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["query_type"] = "symbols",
                ["pattern"] = "*"
            };

            var context = new ToolExecutionContext();

            // Act
            var result = await _tool.ExecuteAsync(parameters, context);

            // Assert
            Assert.True(result.IsSuccessful);
            var resultDict = result.Data as Dictionary<string, object?>;
            var data = resultDict!["data"] as Dictionary<string, object?>;
            var count = Convert.ToInt32(data!["count"]);

            // Should find at least our 3 test classes
            Assert.True(count >= 3, $"Expected to find at least 3 symbols but found {count}");

            var symbols = data["symbols"] as List<Dictionary<string, object?>>;
            var classNames = symbols!.Where(s => s["kind"]?.ToString() == "class")
                                     .Select(s => s["name"]?.ToString())
                                     .ToList();

            Assert.Contains("SimpleAssistantService", classNames);
            Assert.Contains("CodeIndexTool", classNames);
            Assert.Contains("FeedView", classNames);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    [Fact]
    public async Task SearchSymbols_FindsMethods()
    {
        // Arrange
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["query_type"] = "symbols",
                ["pattern"] = "ProcessMessageAsync"
            };

            var context = new ToolExecutionContext();

            // Act
            var result = await _tool.ExecuteAsync(parameters, context);

            // Assert
            Assert.True(result.IsSuccessful);
            var resultDict = result.Data as Dictionary<string, object?>;
            var data = resultDict!["data"] as Dictionary<string, object?>;
            var count = Convert.ToInt32(data!["count"]);

            Assert.True(count > 0, "Expected to find ProcessMessageAsync method");

            var symbols = data["symbols"] as List<Dictionary<string, object?>>;
            Assert.Contains(symbols!, s =>
                s["name"]?.ToString() == "ProcessMessageAsync" &&
                s["kind"]?.ToString() == "method");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    [Fact]
    public async Task GetProjectStructure_ReturnsNamespaces()
    {
        // Arrange
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["query_type"] = "structure"
            };

            var context = new ToolExecutionContext();

            // Act
            var result = await _tool.ExecuteAsync(parameters, context);

            // Assert
            Assert.True(result.IsSuccessful);
            var resultDict = result.Data as Dictionary<string, object?>;
            var data = resultDict!["data"] as Dictionary<string, object?>;

            Assert.NotNull(data);
            Assert.True(data.ContainsKey("structure"));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }

        // Dispose service provider
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
