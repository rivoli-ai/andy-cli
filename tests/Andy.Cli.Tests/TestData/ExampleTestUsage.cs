using Moq;
using Xunit;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Cli.Tests.TestData;

namespace Andy.Cli.Tests.Examples;

/// <summary>
/// Example of how to use the sample responses in tests
/// NOTE: This requires LlmClient.CompleteAsync and StreamCompleteAsync to be virtual
/// </summary>
public class ExampleTestUsage
{
    [Fact]
    public async Task Example_SingleToolCall_WithRealisticResponse()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");

        // Use realistic response from our test data
        mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestResponseHelper.Scenarios.SimpleListFiles());

        // Act - your service would call the LLM
        var response = await mockLlmClient.Object.CompleteAsync(new LlmRequest(), CancellationToken.None);

        // Assert
        Assert.Contains("[Tool Request]", response.Content);
        var toolJson = TestResponseHelper.ExtractToolCallJson(response.Content);
        Assert.NotNull(toolJson);
        Assert.True(TestResponseHelper.IsValidToolCall(toolJson));
        Assert.Contains("list_directory", toolJson);
    }

    [Fact]
    public async Task Example_MultiStepOperation_WithRealisticSequence()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var responses = TestResponseHelper.Scenarios.ProjectSetupSequence();
        var responseQueue = new Queue<LlmResponse>(responses);

        // Return different responses for each call
        mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => responseQueue.Dequeue());

        // Act - simulate multiple LLM calls
        var results = new List<string>();
        for (int i = 0; i < 6; i++) // Project setup has 6 steps
        {
            var response = await mockLlmClient.Object.CompleteAsync(new LlmRequest(), CancellationToken.None);
            results.Add(response.Content);
        }

        // Assert
        Assert.Equal(6, results.Count);
        Assert.Contains("create_directory", results[0]); // First creates directories
        Assert.Contains("write_file", results[3]); // Then creates README
        Assert.Contains(".gitignore", results[4]); // Then creates .gitignore
        Assert.Contains("project is ready", results[5].ToLower()); // Final success message
    }

    [Fact]
    public async Task Example_StreamingResponse_WithRealisticChunks()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var fullResponse = SampleLlmResponses.SingleToolCalls.WriteFile;
        var chunks = TestResponseHelper.CreateStreamingChunks(fullResponse, chunkSize: 20);

        mockLlmClient
            .Setup(x => x.StreamCompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(chunks.ToAsyncEnumerable());

        // Act - collect streaming response
        var collectedResponse = "";
        await foreach (var chunk in mockLlmClient.Object.StreamCompleteAsync(new LlmRequest()))
        {
            collectedResponse += chunk.TextDelta;
        }

        // Assert
        Assert.Equal(fullResponse, collectedResponse);
        Assert.Contains("[Tool Request]", collectedResponse);
        Assert.Contains("write_file", collectedResponse);
    }

    [Fact]
    public async Task Example_ConversationalResponse_NoToolCall()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");

        mockLlmClient
            .Setup(x => x.CompleteAsync(
                It.Is<LlmRequest>(req => req.Messages.Any(m => m.Role == MessageRole.User &&
                    m.Parts.OfType<TextPart>().Any(p => p.Text.Contains("hello")))),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestResponseHelper.Scenarios.NonToolGreeting());

        // Act
        var request = new LlmRequest
        {
            Messages = new List<Message>
            {
                Message.CreateText(MessageRole.User, "hello")
            }
        };
        var response = await mockLlmClient.Object.CompleteAsync(request, CancellationToken.None);

        // Assert
        Assert.DoesNotContain("[Tool Request]", response.Content);
        Assert.Contains("Andy CLI Assistant", response.Content);
        Assert.Contains("help you", response.Content.ToLower());
    }

    [Fact]
    public async Task Example_ErrorRecovery_WithRealisticFlow()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var callCount = 0;

        mockLlmClient
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

        // Act - simulate error and recovery
        var response1 = await mockLlmClient.Object.CompleteAsync(new LlmRequest());
        var response2 = await mockLlmClient.Object.CompleteAsync(new LlmRequest());
        var response3 = await mockLlmClient.Object.CompleteAsync(new LlmRequest());

        // Assert
        Assert.Contains("nonexistent.txt", response1.Content); // First tries invalid file
        Assert.Contains("different approach", response2.Content); // Then recovers
        Assert.Contains("create_directory", response2.Content); // With directory creation
        Assert.Contains("All done", response3.Content); // Finally succeeds
    }
}

// Extension helper for converting to IAsyncEnumerable
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