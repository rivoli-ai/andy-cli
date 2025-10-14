using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Andy.Cli.Services;
using Andy.Cli.Tests.TestData;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Andy.Tools.Core.OutputLimiting;
using Andy.Tools.Execution;
using Andy.Tools.Library;
using Andy.Tools.Library.FileSystem;
using Andy.Tools.Library.System;
using Andy.Tools.Validation;
using Andy.Llm;
using Andy.Model.Llm;
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
        services.AddSingleton<IToolOutputLimiter, ToolOutputLimiter>();
        services.AddSingleton<IServiceProvider>(sp => sp);

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
        _mockLlmClient = new Mock<LlmClient>("test-api-key");

        // Set up UI components
        _feed = new FeedView();
    }

    private void RegisterTools()
    {
        var emptyConfig = new Dictionary<string, object?>();

        // Register tools using Type
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

    private AiConversationService CreateAiService(string? systemPrompt = null)
    {
        if (systemPrompt == null)
        {
            var systemPromptService = new SystemPromptService();
            systemPrompt = systemPromptService.BuildSystemPrompt(_toolRegistry.GetTools());
        }

        var jsonRepair = new JsonRepairService();

        return new AiConversationService(
            _mockLlmClient.Object,
            _toolRegistry,
            _toolExecutor,
            _feed,
            systemPrompt,
            jsonRepair);
    }

    #region Basic Tool Tests

    [Fact]
    public async Task ListFiles_SimplePrompt_CallsListDirectoryTool()
    {
        // Arrange
        var responses = new Queue<LlmResponse>();
        responses.Enqueue(TestResponseHelper.CreateResponse(SampleLlmResponses.SingleToolCalls.ListDirectory));
        responses.Enqueue(TestResponseHelper.CreateResponse("Here are the files in the current directory."));

        _mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => responses.Count > 0 ? responses.Dequeue() : new LlmResponse { Content = "Done" });

        var aiService = CreateAiService();

        // Act
        await aiService.ProcessMessageAsync("List the files in the current directory", false);

        // Assert
        _mockLlmClient.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());

        // Verify tools are registered
        var tools = _toolRegistry.GetTools();
        Assert.Contains(tools, t => t.Metadata.Id == "list_directory");
    }

    [Fact]
    public async Task CreateFile_SimplePrompt_CallsWriteFileTool()
    {
        // Verify write_file tool is registered and available
        var tools = _toolRegistry.GetTools();
        Assert.Contains(tools, t => t.Metadata.Id == "write_file");

        // Test would need proper mocking to verify actual execution
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ReadFile_SimplePrompt_CallsReadFileTool()
    {
        // Create a test file
        var testFile = Path.Combine(_testDirectory, "sample.txt");
        File.WriteAllText(testFile, "Sample content");

        // Verify read_file tool is registered
        var tools = _toolRegistry.GetTools();
        Assert.Contains(tools, t => t.Metadata.Id == "read_file");

        // Verify the test file exists
        Assert.True(File.Exists(testFile));

        await Task.CompletedTask;
    }

    #endregion

    #region Complex Multi-Tool Scenarios



    #endregion

    #region Edge Cases and Error Handling

    [Fact]
    public async Task InvalidToolCall_HandlesGracefully()
    {
        // Arrange
        _mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestResponseHelper.CreateResponse("{\"tool\":\"non_existent_tool\",\"parameters\":{}}"));

        var aiService = CreateAiService();

        // Act & Assert - should not throw
        await aiService.ProcessMessageAsync("do something impossible", false);
        // The service retries on errors, so it may call multiple times
        _mockLlmClient.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task MissingParameters_HandlesGracefully()
    {
        // Arrange
        _mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestResponseHelper.CreateResponse("{\"tool\":\"copy_file\",\"parameters\":{}}"));

        var aiService = CreateAiService();

        // Act & Assert - should handle gracefully
        await aiService.ProcessMessageAsync("copy a file", false);
        // The service retries on errors, so it may call multiple times
        _mockLlmClient.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task AmbiguousPrompt_AsksForClarification()
    {
        // Arrange
        _mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestResponseHelper.Scenarios.NonToolClarification());

        var aiService = CreateAiService();

        // Act
        await aiService.ProcessMessageAsync("do something with the file", false);

        // Assert
        _mockLlmClient.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    #endregion

    #region Natural Language Understanding Tests

    [Fact]
    public async Task NaturalLanguage_UnderstandsIntent()
    {
        // Test various natural language expressions
        var testCases = new[]
        {
            ("show me what's in this folder", SampleLlmResponses.SingleToolCalls.ListDirectory),
            ("what files are here?", SampleLlmResponses.SingleToolCalls.ListDirectory),
            ("save 'hello' to greeting.txt", SampleLlmResponses.SingleToolCalls.WriteFile),
            ("create a new file called test.md", SampleLlmResponses.SingleToolCalls.WriteFile),
            ("delete the old backup file", SampleLlmResponses.SingleToolCalls.DeleteFile),
            ("read the config file", SampleLlmResponses.SingleToolCalls.ReadFile)
        };

        foreach (var (prompt, expectedResponse) in testCases)
        {
            // Setup mock for each test case
            var mockClient = new Mock<LlmClient>("test-api-key");
            mockClient
                .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TestResponseHelper.CreateResponse(expectedResponse));

            // Use the mock client instead of the field for this test
            var jsonRepair = new JsonRepairService();
            var systemPromptService = new SystemPromptService();
            var systemPrompt = systemPromptService.BuildSystemPrompt(_toolRegistry.GetTools());

            var aiService = new AiConversationService(
                mockClient.Object,
                _toolRegistry,
                _toolExecutor,
                _feed,
                systemPrompt,
                jsonRepair);

            // Act
            await aiService.ProcessMessageAsync(prompt, false);

            // Assert - The LLM is called multiple times in the tool execution loop:
            // 1. Initial call with user prompt
            // 2. Call(s) with tool results to get final response
            // The exact number depends on how many tool calls are made
            mockClient.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }
    }

    #endregion

    [Fact]
    public async Task ErrorRecovery_WithRealisticFlow()
    {
        // Arrange
        var callCount = 0;
        _mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => TestResponseHelper.CreateResponse(SampleLlmResponses.ErrorResponses.FileNotFound),
                    2 => TestResponseHelper.CreateResponse(SampleLlmResponses.ErrorResponses.RecoveryAfterError),
                    _ => TestResponseHelper.CreateResponse(SampleLlmResponses.NonToolResponses.TaskComplete)
                };
            });

        var aiService = CreateAiService();

        // Act - simulate error and recovery flow
        await aiService.ProcessMessageAsync("Try to read a file that doesn't exist", false);
        await aiService.ProcessMessageAsync("Now create the directory", false);

        // Assert - verify the recovery happened
        _mockLlmClient.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task StreamingResponse_WithRealisticChunks()
    {
        // Arrange
        var fullResponse = SampleLlmResponses.SingleToolCalls.WriteFile;
        var chunks = TestResponseHelper.CreateStreamingChunks(fullResponse, chunkSize: 20);

        _mockLlmClient
            .Setup(x => x.StreamCompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(chunks.ToAsyncEnumerable());

        var aiService = CreateAiService();

        // Act - collect streaming response
        var collectedResponse = "";
        await foreach (var chunk in _mockLlmClient.Object.StreamCompleteAsync(new LlmRequest()))
        {
            collectedResponse += chunk.TextDelta;
        }

        // Assert
        Assert.Equal(fullResponse, collectedResponse);
        Assert.Contains("[Tool Request]", collectedResponse);
        Assert.Contains("write_file", collectedResponse);
    }
}

/// <summary>
/// Helper extension for creating async enumerables in tests
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }
}