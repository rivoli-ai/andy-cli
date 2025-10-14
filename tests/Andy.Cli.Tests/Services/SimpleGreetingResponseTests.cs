using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class SimpleGreetingResponseTests
{
    private readonly Mock<LlmClient> _mockLlmClient;
    private readonly Mock<IToolRegistry> _mockToolRegistry;
    private readonly Mock<IToolExecutor> _mockToolExecutor;
    private readonly FeedView _feedView;
    private readonly Mock<IJsonRepairService> _mockJsonRepair;
    private readonly AiConversationService _service;

    public SimpleGreetingResponseTests()
    {
        _mockLlmClient = new Mock<LlmClient>("test-api-key");
        _mockToolRegistry = new Mock<IToolRegistry>();
        _mockToolExecutor = new Mock<IToolExecutor>();
        _feedView = new FeedView();
        _mockJsonRepair = new Mock<IJsonRepairService>();

        // Setup tool registry to return empty list (no tools for greetings)
        _mockToolRegistry.Setup(x => x.GetTools(
            It.IsAny<ToolCategory?>(),
            It.IsAny<ToolCapability?>(),
            It.IsAny<IEnumerable<string>?>(),
            It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());

        _service = new AiConversationService(
            _mockLlmClient.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _feedView,
            "You are a helpful assistant.",
            _mockJsonRepair.Object,
            logger: null,
            modelName: "llama-3.3-70b",
            providerName: "cerebras"
        );
    }

    [Fact]
    public async Task Should_Display_Simple_Greeting_Response()
    {
        // Arrange
        var expectedResponse = "Hello! How can I assist you today?";
        
        _mockLlmClient.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = expectedResponse,
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 15, TotalTokens = 25 }
            });

        // Act
        var result = await _service.ProcessMessageAsync("hello", enableStreaming: false);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Hello", result);
        Assert.Contains("assist", result);
        
        // Verify that the LLM was called exactly once
        _mockLlmClient.Verify(x => x.CompleteAsync(
            It.Is<LlmRequest>(req => 
                req.Messages != null && 
                req.Messages.Count == 1 && 
                req.Messages[0].Parts != null &&
                req.Messages[0].Parts.Count > 0 &&
                req.Messages[0].Parts.Any(p => p is TextPart && ((TextPart)p).Text == "hello") &&
                req.Tools == null // No tools for simple greetings
            ), 
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task Should_Not_Include_Tools_For_Simple_Greeting()
    {
        // Arrange
        var expectedResponse = "Hi there! What can I help you with?";
        
        _mockLlmClient.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = expectedResponse,
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 15, TotalTokens = 25 }
            });

        // Act
        await _service.ProcessMessageAsync("hello", enableStreaming: false);

        // Assert - Verify no tools were included in the request
        _mockLlmClient.Verify(x => x.CompleteAsync(
            It.Is<LlmRequest>(req => req.Tools == null || req.Tools.Count == 0), 
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task Should_Handle_Empty_Response_Gracefully()
    {
        // Arrange
        _mockLlmClient.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = "", // Empty response
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 0, TotalTokens = 10 }
            });

        // Act
        var result = await _service.ProcessMessageAsync("hello", enableStreaming: false);

        // Assert
        Assert.NotNull(result);
        // Should have some fallback response or error message
        Assert.NotEqual("", result);
    }

    [Fact]
    public async Task Should_Display_Response_For_Various_Greetings()
    {
        // Arrange
        var greetings = new[] { "hi", "hey", "good morning", "good afternoon", "good evening" };
        var expectedResponse = "Greetings! How may I help you?";
        
        _mockLlmClient.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = expectedResponse,
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 15, TotalTokens = 25 }
            });

        foreach (var greeting in greetings)
        {
            // Act
            var result = await _service.ProcessMessageAsync(greeting, enableStreaming: false);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("Greetings", result);
            
            // Verify no tools were included for any greeting
            _mockLlmClient.Verify(x => x.CompleteAsync(
                It.Is<LlmRequest>(req => req.Tools == null || req.Tools.Count == 0), 
                It.IsAny<CancellationToken>()
            ), Times.AtLeastOnce);
        }
    }

    [Fact]
    public async Task Should_Parse_And_Display_Llama_Plain_Text_Response()
    {
        // Arrange - Llama models return plain text, not structured JSON
        var llamaResponse = "I'm happy to chat with you. How can I assist you today?";
        
        _mockLlmClient.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = llamaResponse,
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 15, TotalTokens = 25 }
            });

        // Act
        var result = await _service.ProcessMessageAsync("hello", enableStreaming: false);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("happy to chat", result);
        Assert.Contains("assist you today", result);
        
        // The full response should be displayed, not truncated
        Assert.Equal(llamaResponse, result.Trim());
    }

    [Fact]
    public async Task Should_Handle_Response_With_Colon_Ending()
    {
        // Arrange - Test the edge case where response ends with colon
        var responseWithColon = "No special action is required in this case:";
        
        _mockLlmClient.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = responseWithColon,
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 15, TotalTokens = 25 }
            });

        // Act
        var result = await _service.ProcessMessageAsync("hello", enableStreaming: false);

        // Assert
        Assert.NotNull(result);
        // Even responses ending with colon should be displayed
        Assert.Contains("No special action is required", result);
        
        // Check that we're using the raw response when it seems incomplete
        Assert.Equal(responseWithColon, result.Trim());
    }
}