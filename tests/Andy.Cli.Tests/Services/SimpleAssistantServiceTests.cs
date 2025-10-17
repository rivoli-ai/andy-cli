using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Tests for SimpleAssistantService - the core integration layer
/// </summary>
public class SimpleAssistantServiceTests
{
    private readonly Mock<ILlmProvider> _mockLlmProvider;
    private readonly Mock<IToolRegistry> _mockToolRegistry;
    private readonly Mock<IToolExecutor> _mockToolExecutor;
    private readonly Mock<FeedView> _mockFeedView;
    private readonly Mock<ILogger<SimpleAssistantService>> _mockLogger;

    public SimpleAssistantServiceTests()
    {
        _mockLlmProvider = new Mock<ILlmProvider>();
        _mockToolRegistry = new Mock<IToolRegistry>();
        _mockToolExecutor = new Mock<IToolExecutor>();
        _mockFeedView = new Mock<FeedView>(MockBehavior.Loose);
        _mockLogger = new Mock<ILogger<SimpleAssistantService>>();

        // Setup basic tool registry
        _mockToolRegistry.Setup(x => x.GetTools()).Returns(new List<IToolDefinition>());
    }

    [Fact]
    public async Task ProcessMessageAsync_WithSimpleMessage_ReturnsResponse()
    {
        // Arrange
        var expectedResponse = "Test response";
        var mockResponse = new LlmResponse
        {
            AssistantMessage = new Model.Model.Message
            {
                Role = Model.Model.MessageRole.Assistant,
                Content = expectedResponse
            },
            InputTokens = 10,
            OutputTokens = 5
        };

        _mockLlmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse);

        var service = new SimpleAssistantService(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockFeedView.Object,
            "test-model",
            "test-provider",
            tokenCounter: null,
            logger: _mockLogger.Object
        );

        // Act
        var result = await service.ProcessMessageAsync("Hello", enableStreaming: false);

        // Assert
        Assert.Equal(expectedResponse, result);
        _mockLlmProvider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessageAsync_WithToolCall_ExecutesTool()
    {
        // Arrange
        var toolCallResponse = new LlmResponse
        {
            AssistantMessage = new Model.Model.Message
            {
                Role = Model.Model.MessageRole.Assistant,
                Content = null,
                ToolCalls = new List<Model.Model.ToolCall>
                {
                    new Model.Model.ToolCall
                    {
                        Id = "test-tool-id",
                        Name = "test_tool",
                        Arguments = "{}"
                    }
                }
            },
            InputTokens = 10,
            OutputTokens = 5
        };

        var finalResponse = new LlmResponse
        {
            AssistantMessage = new Model.Model.Message
            {
                Role = Model.Model.MessageRole.Assistant,
                Content = "Tool result processed"
            },
            InputTokens = 15,
            OutputTokens = 10
        };

        var callCount = 0;
        _mockLlmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callCount++ == 0 ? toolCallResponse : finalResponse);

        _mockToolExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>()))
            .ReturnsAsync(new ToolExecutionResult(true, "Tool executed"));

        var service = new SimpleAssistantService(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockFeedView.Object,
            "test-model",
            "test-provider",
            tokenCounter: null,
            logger: _mockLogger.Object
        );

        // Act
        var result = await service.ProcessMessageAsync("Execute tool", enableStreaming: false);

        // Assert
        Assert.Equal("Tool result processed", result);
        _mockToolExecutor.Verify(x => x.ExecuteAsync("test_tool", It.IsAny<Dictionary<string, object?>>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessageAsync_WithCwdQuery_ReturnsCwdDirectly()
    {
        // Arrange
        var service = new SimpleAssistantService(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockFeedView.Object,
            "test-model",
            "test-provider",
            tokenCounter: null,
            logger: _mockLogger.Object
        );

        // Act
        var result = await service.ProcessMessageAsync("what is the current directory?", enableStreaming: false);

        // Assert
        Assert.Contains("Current directory:", result);
        // Should NOT call LLM for this simple query
        _mockLlmProvider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ClearContext_ClearsHistory()
    {
        // Arrange
        var service = new SimpleAssistantService(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockFeedView.Object,
            "test-model",
            "test-provider",
            tokenCounter: null,
            logger: _mockLogger.Object
        );

        // Act
        service.ClearContext();

        // Assert - no exception means success (history cleared internally)
        var stats = service.GetContextStats();
        Assert.Equal(0, stats.TurnCount);
    }

    [Fact]
    public async Task ProcessMessageAsync_UpdatesTokenCounter()
    {
        // Arrange
        var tokenCounter = new TokenCounter();
        var mockResponse = new LlmResponse
        {
            AssistantMessage = new Model.Model.Message
            {
                Role = Model.Model.MessageRole.Assistant,
                Content = "Response"
            },
            InputTokens = 10,
            OutputTokens = 5
        };

        _mockLlmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse);

        var service = new SimpleAssistantService(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockFeedView.Object,
            "test-model",
            "test-provider",
            tokenCounter: tokenCounter,
            logger: _mockLogger.Object
        );

        // Act
        await service.ProcessMessageAsync("Hello", enableStreaming: false);

        // Assert
        Assert.True(tokenCounter.TotalTokens > 0, "Token counter should be updated");
    }
}
