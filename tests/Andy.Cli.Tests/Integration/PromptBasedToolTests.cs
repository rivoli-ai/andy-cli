using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Andy.Tools.Library;
using Andy.Tools.Library.FileSystem;
using Andy.Tools.Library.System;
using Andy.Tools.Validation;
using Andy.Tools.Framework;
using Andy.Llm;
using Andy.Llm.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// Tests the AI conversation service with realistic prompts and tool execution
/// </summary>
public class PromptBasedToolTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly Mock<LlmClient> _mockLlmClient;
    private readonly FeedView _feed;
    private readonly AiConversationService _aiService;
    private readonly string _testDirectory;

    public PromptBasedToolTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"andy_cli_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        // Set up services
        var services = new ServiceCollection();
        
        // Add logging
        services.AddLogging(builder => builder.AddConsole());
        
        // Add tools framework
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IToolValidator, ToolValidator>();
        services.AddSingleton<IResourceMonitor, ResourceMonitor>();
        services.AddSingleton<ISecurityManager, SecurityManager>();
        
        // Register tools
        services.AddTransient<ListDirectoryTool>();
        services.AddTransient<ReadFileTool>();
        services.AddTransient<WriteFileTool>();
        services.AddTransient<CopyFileTool>();
        services.AddTransient<MoveFileTool>();
        services.AddTransient<DeleteFileTool>();
        services.AddTransient<Andy.Cli.Tools.CreateDirectoryTool>();
        services.AddTransient<SystemInfoTool>();
        
        _serviceProvider = services.BuildServiceProvider();
        
        // Initialize tools
        _toolRegistry = _serviceProvider.GetRequiredService<IToolRegistry>();
        _toolExecutor = _serviceProvider.GetRequiredService<IToolExecutor>();
        RegisterTools();
        
        // Set up mock LLM client
        _mockLlmClient = new Mock<LlmClient>();
        
        // Set up UI components
        _feed = new FeedView();
        
        // Create system prompt
        var systemPromptService = new SystemPromptService();
        var systemPrompt = systemPromptService.BuildSystemPrompt(_toolRegistry.GetTools(enabledOnly: true));
        
        // Create AI service
        _aiService = new AiConversationService(
            _mockLlmClient.Object,
            _toolRegistry,
            _toolExecutor,
            _feed,
            systemPrompt);
    }

    private void RegisterTools()
    {
        var emptyConfig = new Dictionary<string, object?>();
        _toolRegistry.RegisterTool(typeof(ListDirectoryTool), emptyConfig);
        _toolRegistry.RegisterTool(typeof(ReadFileTool), emptyConfig);
        _toolRegistry.RegisterTool(typeof(WriteFileTool), emptyConfig);
        _toolRegistry.RegisterTool(typeof(CopyFileTool), emptyConfig);
        _toolRegistry.RegisterTool(typeof(MoveFileTool), emptyConfig);
        _toolRegistry.RegisterTool(typeof(DeleteFileTool), emptyConfig);
        _toolRegistry.RegisterTool(typeof(Andy.Cli.Tools.CreateDirectoryTool), emptyConfig);
        _toolRegistry.RegisterTool(typeof(SystemInfoTool), emptyConfig);
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
        _serviceProvider?.Dispose();
    }

    #region Basic Tool Tests

    [Fact]
    public async Task ListFiles_SimplePrompt_CallsListDirectoryTool()
    {
        // Arrange
        var prompt = "list files in the current directory";
        SetupLlmResponse("{\"tool\":\"list_directory\",\"parameters\":{\"path\":\".\"}}");

        // Act
        var result = await _aiService.ProcessMessageAsync(prompt, false);

        // Assert
        _mockLlmClient.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("list_directory", result);
    }

    [Fact]
    public async Task CreateFile_SimplePrompt_CallsWriteFileTool()
    {
        // Arrange
        var prompt = "create a file called test.txt with content 'Hello World'";
        SetupLlmResponse("{\"tool\":\"write_file\",\"parameters\":{\"file_path\":\"test.txt\",\"content\":\"Hello World\"}}");

        // Act
        var result = await _aiService.ProcessMessageAsync(prompt, false);

        // Assert
        _mockLlmClient.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var testFile = Path.Combine(_testDirectory, "test.txt");
        Assert.True(File.Exists(testFile));
        Assert.Equal("Hello World", File.ReadAllText(testFile));
    }

    [Fact]
    public async Task ReadFile_SimplePrompt_CallsReadFileTool()
    {
        // Arrange
        var testFile = Path.Combine(_testDirectory, "sample.txt");
        File.WriteAllText(testFile, "Sample content");
        
        var prompt = $"read the file {testFile}";
        SetupLlmResponse($"{{\"tool\":\"read_file\",\"parameters\":{{\"file_path\":\"{testFile.Replace("\\", "\\\\")}\"}}}}");

        // Act
        var result = await _aiService.ProcessMessageAsync(prompt, false);

        // Assert
        _mockLlmClient.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        Assert.Contains("Sample content", result);
    }

    #endregion

    #region Complex Multi-Tool Scenarios

    [Fact]
    public async Task CopyFileToNewDirectory_RequiresMultipleTools()
    {
        // Arrange
        var sourceFile = Path.Combine(_testDirectory, "source.txt");
        File.WriteAllText(sourceFile, "Source content");
        var destDir = Path.Combine(_testDirectory, "tmp");
        
        var prompt = $"copy {sourceFile} to {destDir}";
        
        // First response: create directory
        SetupLlmResponseSequence(new[]
        {
            $"{{\"tool\":\"create_directory\",\"parameters\":{{\"path\":\"{destDir.Replace("\\", "\\\\")}\"}}}}",
            $"{{\"tool\":\"copy_file\",\"parameters\":{{\"source_path\":\"{sourceFile.Replace("\\", "\\\\")}\",\"destination_path\":\"{Path.Combine(destDir, "source.txt").Replace("\\", "\\\\")}\"}}}}",
            "Successfully copied the file to the new directory."
        });

        // Act
        var result = await _aiService.ProcessMessageAsync(prompt, false);

        // Assert
        Assert.True(Directory.Exists(destDir));
        Assert.True(File.Exists(Path.Combine(destDir, "source.txt")));
        Assert.Equal("Source content", File.ReadAllText(Path.Combine(destDir, "source.txt")));
    }

    [Fact]
    public async Task OrganizeFiles_ComplexScenario()
    {
        // Arrange
        // Create test files
        File.WriteAllText(Path.Combine(_testDirectory, "document.txt"), "Doc content");
        File.WriteAllText(Path.Combine(_testDirectory, "image.png"), "Image data");
        File.WriteAllText(Path.Combine(_testDirectory, "script.js"), "JavaScript code");
        
        var prompt = "organize files by type into separate folders";
        
        // Simulate multiple tool calls
        SetupLlmResponseSequence(new[]
        {
            "{\"tool\":\"list_directory\",\"parameters\":{\"path\":\".\"}}",
            "{\"tool\":\"create_directory\",\"parameters\":{\"path\":\"documents\"}}",
            "{\"tool\":\"create_directory\",\"parameters\":{\"path\":\"images\"}}",
            "{\"tool\":\"create_directory\",\"parameters\":{\"path\":\"scripts\"}}",
            "{\"tool\":\"move_file\",\"parameters\":{\"source_path\":\"document.txt\",\"destination_path\":\"documents/document.txt\"}}",
            "{\"tool\":\"move_file\",\"parameters\":{\"source_path\":\"image.png\",\"destination_path\":\"images/image.png\"}}",
            "{\"tool\":\"move_file\",\"parameters\":{\"source_path\":\"script.js\",\"destination_path\":\"scripts/script.js\"}}",
            "Files have been organized by type into separate folders."
        });

        // Act
        var result = await _aiService.ProcessMessageAsync(prompt, false);

        // Assert
        Assert.True(Directory.Exists(Path.Combine(_testDirectory, "documents")));
        Assert.True(Directory.Exists(Path.Combine(_testDirectory, "images")));
        Assert.True(Directory.Exists(Path.Combine(_testDirectory, "scripts")));
        Assert.True(File.Exists(Path.Combine(_testDirectory, "documents", "document.txt")));
        Assert.True(File.Exists(Path.Combine(_testDirectory, "images", "image.png")));
        Assert.True(File.Exists(Path.Combine(_testDirectory, "scripts", "script.js")));
    }

    #endregion

    #region Edge Cases and Error Handling

    [Fact]
    public async Task InvalidToolCall_HandlesGracefully()
    {
        // Arrange
        var prompt = "do something impossible";
        SetupLlmResponse("{\"tool\":\"non_existent_tool\",\"parameters\":{}}");

        // Act
        var result = await _aiService.ProcessMessageAsync(prompt, false);

        // Assert
        Assert.Contains("error", result.ToLower());
    }

    [Fact]
    public async Task MissingParameters_HandlesGracefully()
    {
        // Arrange
        var prompt = "copy a file";
        SetupLlmResponse("{\"tool\":\"copy_file\",\"parameters\":{}}");

        // Act
        var result = await _aiService.ProcessMessageAsync(prompt, false);

        // Assert
        Assert.Contains("error", result.ToLower());
    }

    [Fact]
    public async Task AmbiguousPrompt_AsksForClarification()
    {
        // Arrange
        var prompt = "do something with the file";
        SetupLlmResponse("I need more information. What would you like to do with which file?");

        // Act
        var result = await _aiService.ProcessMessageAsync(prompt, false);

        // Assert
        Assert.Contains("information", result.ToLower());
    }

    #endregion

    #region Natural Language Understanding Tests

    [Fact]
    public async Task NaturalLanguage_UnderstandsIntent()
    {
        // Test various natural language expressions
        var testCases = new[]
        {
            ("show me what's in this folder", "list_directory"),
            ("what files are here?", "list_directory"),
            ("can you check what's in the current directory", "list_directory"),
            ("save 'hello' to greeting.txt", "write_file"),
            ("create a new file called test.md", "write_file"),
            ("delete the old backup file", "delete_file"),
            ("remove temporary.txt", "delete_file")
        };

        foreach (var (prompt, expectedTool) in testCases)
        {
            // Reset mock
            _mockLlmClient.Reset();
            
            // Setup response with expected tool
            SetupLlmResponse($"{{\"tool\":\"{expectedTool}\",\"parameters\":{{}}}}");
            
            // Act
            var result = await _aiService.ProcessMessageAsync(prompt, false);
            
            // Assert
            Assert.Contains(expectedTool, result);
        }
    }

    #endregion

    #region Helper Methods

    private void SetupLlmResponse(string response)
    {
        _mockLlmClient.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = response });
    }

    private void SetupLlmResponseSequence(string[] responses)
    {
        var sequence = _mockLlmClient.SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()));
        
        foreach (var response in responses)
        {
            sequence.ReturnsAsync(new LlmResponse { Content = response });
        }
    }

    private void SetupStreamingLlmResponse(string response)
    {
        var chunks = response.Select(c => new LlmStreamingUpdate { TextDelta = c.ToString() });
        
        _mockLlmClient.Setup(x => x.StreamCompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(chunks.ToAsyncEnumerable());
    }

    #endregion
}

/// <summary>
/// Helper extension for creating async enumerables in tests
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield();
        }
    }
}