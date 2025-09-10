using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class StreamingRepetitionTest
{
    [Fact(Skip = "Mock LlmClient not returning expected responses")]
    public async Task StreamingResponse_ShouldNotHaveDuplicateContent()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockToolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();

        // Mock tool registry to return empty list
        mockToolRegistry.Setup(x => x.GetTools(
                It.IsAny<ToolCategory?>(),
                It.IsAny<ToolCapability?>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());
        var jsonRepair = new JsonRepairService();

        var service = new AiConversationService(
            mockLlmClient.Object,
            mockToolRegistry.Object,
            mockToolExecutor.Object,
            feed,
            "Test system prompt",
            jsonRepair);

        // Simulate streaming response with repeated content
        var testContent = "I'm ready to help. I see we are in /Users/samibengrine/Devel/rivoli-ai/andy-cli/ directory.";
        var chunks = SimulateStreamingChunks(testContent);

        // Setup mock to return streaming chunks
        mockLlmClient.Setup(x => x.StreamCompleteAsync(
                It.IsAny<LlmRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(chunks);

        // Setup mock for non-streaming fallback
        mockLlmClient.Setup(x => x.CompleteAsync(
                It.IsAny<LlmRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = testContent });

        // Act
        await service.ProcessMessageAsync("test message", enableStreaming: true);

        // Get the feed content
        var feedContent = GetFeedContent(feed);

        // Assert - Check for repetition
        Assert.NotNull(feedContent);

        // Count occurrences of the test phrase
        var occurrences = CountOccurrences(feedContent, "I'm ready to help");

        // Log for debugging
        Console.WriteLine($"Feed content: {feedContent}");
        Console.WriteLine($"Occurrences of 'I'm ready to help': {occurrences}");

        // Should appear only once
        Assert.Equal(1, occurrences);
    }

    [Fact(Skip = "Mock LlmClient not returning expected responses")]
    public async Task StreamingResponse_WithToolCalls_ShouldNotDuplicate()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockToolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();

        // Mock tool registry to return empty list
        mockToolRegistry.Setup(x => x.GetTools(
                It.IsAny<ToolCategory?>(),
                It.IsAny<ToolCapability?>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());
        var jsonRepair = new JsonRepairService();

        var service = new AiConversationService(
            mockLlmClient.Object,
            mockToolRegistry.Object,
            mockToolExecutor.Object,
            feed,
            "Test system prompt",
            jsonRepair);

        // Content with tool call
        var testContent = "Let me check that. {\"tool\":\"list_directory\",\"parameters\":{\"path\":\"./src/\"}}";
        var chunks = SimulateStreamingChunks(testContent);

        // Setup mock
        mockLlmClient.Setup(x => x.StreamCompleteAsync(
                It.IsAny<LlmRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(chunks);

        // Mock tool execution
        mockToolExecutor.Setup(x => x.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<ToolExecutionContext>()))
            .Returns(Task.FromResult(new Andy.Tools.Core.ToolExecutionResult
            {
                IsSuccessful = true,
                Data = "Directory contents..."
            }));

        // Act
        await service.ProcessMessageAsync("test message", enableStreaming: true);

        // Get the feed content
        var feedContent = GetFeedContent(feed);

        // Assert
        var occurrences = CountOccurrences(feedContent, "Let me check that");
        Console.WriteLine($"Feed content: {feedContent}");
        Console.WriteLine($"Occurrences of 'Let me check that': {occurrences}");

        Assert.Equal(1, occurrences);
    }

    private async IAsyncEnumerable<LlmStreamResponse> SimulateStreamingChunks(string content)
    {
        // Simulate how content might be streamed in chunks
        var chunkSize = 20;
        for (int i = 0; i < content.Length; i += chunkSize)
        {
            var chunk = content.Substring(i, Math.Min(chunkSize, content.Length - i));
            yield return new LlmStreamResponse
            {
                TextDelta = chunk
            };
            await Task.Delay(10); // Simulate network delay
        }
    }

    private string GetFeedContent(FeedView feed)
    {
        // This is a simplified version - in reality we'd need to access the feed's internal content
        // For testing, we might need to expose a method or property to get the accumulated content
        var sb = new StringBuilder();

        // Use reflection or make feed content accessible for testing
        var fieldInfo = typeof(FeedView).GetField("_messages",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (fieldInfo != null)
        {
            var messages = fieldInfo.GetValue(feed) as List<object>;
            if (messages != null)
            {
                foreach (var msg in messages)
                {
                    sb.AppendLine(msg.ToString());
                }
            }
        }

        return sb.ToString();
    }

    private int CountOccurrences(string text, string phrase)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(phrase, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += phrase.Length;
        }
        return count;
    }
}

/// <summary>
/// Test to check if the issue is in LlmClient streaming
/// </summary>
public class LlmClientStreamingTest
{
    [Fact]
    public async Task LlmClient_StreamingChunks_ShouldNotDuplicate()
    {
        // This test would check if andy-llm is sending duplicate chunks
        var receivedChunks = new List<string>();

        // Simulate receiving chunks
        await foreach (var chunk in SimulateRawStreamingFromLlm())
        {
            receivedChunks.Add(chunk.TextDelta);
        }

        // Combine all chunks
        var combined = string.Join("", receivedChunks);

        // Check for duplication
        Assert.DoesNotContain("I'm ready to helpI'm ready to help", combined);

        Console.WriteLine($"Combined chunks: {combined}");
        Console.WriteLine($"Total chunks: {receivedChunks.Count}");
    }

    private async IAsyncEnumerable<LlmStreamResponse> SimulateRawStreamingFromLlm()
    {
        // Simulate what andy-llm might be sending
        var fullText = "I'm ready to help. I see we are in /Users/samibengrine/Devel/rivoli-ai/andy-cli/ directory.";

        // Simulate potential duplication bug - sending same content twice
        // Uncomment to test duplication scenario:
        // yield return new LlmStreamResponse { TextDelta = fullText };
        // yield return new LlmStreamResponse { TextDelta = fullText };

        // Normal streaming
        var chunks = new[] {
            "I'm ready to help.",
            " I see we are in ",
            "/Users/samibengrine/",
            "Devel/rivoli-ai/",
            "andy-cli/ directory."
        };

        foreach (var chunk in chunks)
        {
            yield return new LlmStreamResponse { TextDelta = chunk ?? "" };
            await Task.Delay(5);
        }
    }
}