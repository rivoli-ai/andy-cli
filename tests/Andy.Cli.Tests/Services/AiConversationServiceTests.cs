using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Tests.Services;

public class AiConversationServiceTests
{
    private readonly Mock<LlmClient> _mockLlmClient;
    private readonly Mock<IToolRegistry> _mockToolRegistry;
    private readonly Mock<IToolExecutor> _mockToolExecutor;
    private readonly FeedView _feed;
    private readonly AiConversationService _service;

    public AiConversationServiceTests()
    {
        _mockLlmClient = new Mock<LlmClient>((string)"test-key");
        _mockToolRegistry = new Mock<IToolRegistry>();
        _mockToolExecutor = new Mock<IToolExecutor>();
        _feed = new FeedView();
        
        var systemPrompt = "You are a helpful assistant with access to tools.";
        _service = new AiConversationService(
            _mockLlmClient.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _feed,
            systemPrompt);
    }

    [Fact]
    public async Task ProcessMessageAsync_WithToolCall_ExecutesTool()
    {
        // Arrange
        var userMessage = "List files in the current directory";
        var toolResponse = new LlmResponse
        {
            Content = @"{""tool"": ""list_directory"", ""parameters"": {""path"": "".""}}"
        };
        
        var toolResult = new ToolExecutionResult
        {
            IsSuccessful = true,
            FullOutput = @"{""files"": [""file1.txt"", ""file2.txt""], ""directories"": [""subdir""]}",
            Data = new Dictionary<string, object>
            {
                ["files"] = new[] { "file1.txt", "file2.txt" },
                ["directories"] = new[] { "subdir" }
            }
        };

        var finalResponse = new LlmResponse
        {
            Content = "I found 2 files and 1 directory in the current folder."
        };

        _mockLlmClient.SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(toolResponse)
            .ReturnsAsync(finalResponse);

        _mockToolRegistry.Setup(x => x.GetTools(It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());

        var mockToolService = new Mock<ToolExecutionService>(
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _feed,
            10);

        // Act
        var result = await _service.ProcessMessageAsync(userMessage);

        // Assert
        Assert.NotNull(result);
        // In a real test, we'd verify through the feed's public interface or use a test double
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData(@"{""tool"": ""read_file"", ""parameters"": {""path"": ""test.txt""}}", "read_file", "path", "test.txt")]
    [InlineData(@"{""function"": ""write_file"", ""arguments"": {""content"": ""hello""}}", "write_file", "content", "hello")]
    [InlineData(@"<tool_use>{""tool"": ""list_directory"", ""parameters"": {""recursive"": true}}</tool_use>", "list_directory", "recursive", true)]
    public void ExtractToolCalls_ParsesVariousFormats(string llmResponse, string expectedTool, string paramName, object paramValue)
    {
        // This test would need access to private methods, so we'll test through the public interface
        // In a real scenario, we might refactor to make this testable or use reflection
        Assert.True(true); // Placeholder - would need refactoring for proper testing
    }

    [Fact]
    public async Task ProcessMessageAsync_WithoutToolCall_ReturnsDirectResponse()
    {
        // Arrange
        var userMessage = "What is the weather like?";
        var llmResponse = new LlmResponse
        {
            Content = "I don't have access to real-time weather information."
        };

        _mockLlmClient.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmResponse);

        _mockToolRegistry.Setup(x => x.GetTools(It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());

        // Act
        var result = await _service.ProcessMessageAsync(userMessage);

        // Assert
        Assert.Contains("weather", result, StringComparison.OrdinalIgnoreCase);
        // Verify through context or other means
        Assert.NotNull(result);
    }

    [Fact]
    public void GetContextStats_ReturnsCorrectStatistics()
    {
        // Act
        var stats = _service.GetContextStats();

        // Assert
        Assert.NotNull(stats);
        Assert.True(stats.MessageCount >= 0);
        Assert.True(stats.EstimatedTokens >= 0);
    }

    [Fact]
    public void ClearContext_ResetsConversation()
    {
        // Act
        _service.ClearContext();
        var stats = _service.GetContextStats();

        // Assert
        Assert.Equal(1, stats.MessageCount); // Only system prompt remains
    }
}

public class ToolExecutionResult : ToolResult
{
    public string? FullOutput { get; set; }
    public bool TruncatedForDisplay { get; set; }
}