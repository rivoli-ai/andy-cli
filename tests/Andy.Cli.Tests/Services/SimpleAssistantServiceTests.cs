using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly FeedView _feedView;

    public SimpleAssistantServiceTests()
    {
        _mockLlmProvider = new Mock<ILlmProvider>();
        _mockToolRegistry = new Mock<IToolRegistry>();
        _mockToolExecutor = new Mock<IToolExecutor>();
        // FeedView is sealed; use a real instance (mocking is not possible).
        _feedView = new FeedView();

        // Setup basic tool registry (GetTools now returns ToolRegistration entries).
        _mockToolRegistry.Setup(x => x.GetTools(
                It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());
        // The engine's SimpleAgent builds its tool declarations from IToolRegistry.Tools
        // (registry.Tools.Where(t => t.IsEnabled)). A real registry always returns a non-null
        // list; an unconfigured Moq property returns null, which makes .Where() throw
        // ArgumentNullException("source") inside the agent loop. Return an empty list so the
        // agent runs against a valid (tool-less) registry.
        _mockToolRegistry.Setup(x => x.Tools).Returns(new List<ToolRegistration>());
    }

    // End-to-end: a simple assistant response flows through the packaged SimpleAgent loop and is
    // returned. (Requires the IToolRegistry.Tools mock set up in the constructor; without it the
    // agent's tool-declaration build throws ArgumentNullException on the null registry list.)
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
                Content = expectedResponse,
                ToolCalls = new List<Model.Model.ToolCall>()
            },
            Usage = new LlmUsage { PromptTokens = 10, CompletionTokens = 5 }
        };

        _mockLlmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse);

        var service = new SimpleAssistantService(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _feedView,
            "test-model",
            "test-provider",
            tokenCounter: null,
            loggerFactory: NullLoggerFactory.Instance
        );

        // Act
        var result = await service.ProcessMessageAsync("Hello", enableStreaming: false);

        // Assert
        Assert.Equal(expectedResponse, result);
        _mockLlmProvider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // End-to-end: a tool-call response flows through the packaged SimpleAgent loop to the executor,
    // and the follow-up response is returned. (Also relies on the IToolRegistry.Tools mock.)
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
            Usage = new LlmUsage { PromptTokens = 10, CompletionTokens = 5 }
        };

        var finalResponse = new LlmResponse
        {
            AssistantMessage = new Model.Model.Message
            {
                Role = Model.Model.MessageRole.Assistant,
                Content = "Tool result processed",
                ToolCalls = new List<Model.Model.ToolCall>()
            },
            Usage = new LlmUsage { PromptTokens = 15, CompletionTokens = 10 }
        };

        var callCount = 0;
        _mockLlmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callCount++ == 0 ? toolCallResponse : finalResponse);

        _mockToolExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new ToolExecutionResult { IsSuccessful = true, Data = "Tool executed" });

        var service = new SimpleAssistantService(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _feedView,
            "test-model",
            "test-provider",
            tokenCounter: null,
            loggerFactory: NullLoggerFactory.Instance
        );

        // Act
        var result = await service.ProcessMessageAsync("Execute tool", enableStreaming: false);

        // Assert
        Assert.Equal("Tool result processed", result);
        _mockToolExecutor.Verify(x => x.ExecuteAsync("test_tool", It.IsAny<Dictionary<string, object?>>(), It.IsAny<ToolExecutionContext?>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessageAsync_WithCwdQuery_ReturnsCwdDirectly()
    {
        // Arrange
        var service = new SimpleAssistantService(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _feedView,
            "test-model",
            "test-provider",
            tokenCounter: null,
            loggerFactory: NullLoggerFactory.Instance
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
            _feedView,
            "test-model",
            "test-provider",
            tokenCounter: null,
            loggerFactory: NullLoggerFactory.Instance
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
                Content = "Response",
                ToolCalls = new List<Model.Model.ToolCall>()
            },
            Usage = new LlmUsage { PromptTokens = 10, CompletionTokens = 5 }
        };

        _mockLlmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse);

        var service = new SimpleAssistantService(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _feedView,
            "test-model",
            "test-provider",
            tokenCounter: tokenCounter,
            loggerFactory: NullLoggerFactory.Instance
        );

        // Act
        await service.ProcessMessageAsync("Hello", enableStreaming: false);

        // Assert
        Assert.True(tokenCounter.TotalInputTokens + tokenCounter.TotalOutputTokens > 0, "Token counter should be updated");
    }
}
