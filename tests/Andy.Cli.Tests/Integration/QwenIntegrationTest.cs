using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// Integration test for Qwen model interaction
/// </summary>
public class QwenIntegrationTest
{
    private static async IAsyncEnumerable<LlmStreamResponse> GetStream(string content)
    {
        yield return new LlmStreamResponse { TextDelta = content };
        yield return new LlmStreamResponse { IsComplete = true, FinishReason = "stop" };
        await Task.CompletedTask;
    }
    [Fact]
    public async Task ProcessQwenResponse_ExtractsToolCall_ExecutesCorrectly()
    {
        // Arrange - Mock components
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockToolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();

        // Setup JSON repair service
        var jsonRepair = new JsonRepairService();

        // Mock LLM to return Qwen-style response with tool call
        var qwenResponse = @"}
{
  ""tool_call"": {
    ""name"": ""list_directory"",
    ""arguments"": {
      ""directory_path"": "".""
    }
  }
}";

        mockLlmClient.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), default))
            .ReturnsAsync(new LlmResponse
            {
                Content = qwenResponse,
                FinishReason = "stop"
            });

        // Also support streaming path returning the same content
        mockLlmClient.Setup(x => x.StreamCompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns((LlmRequest _, CancellationToken __) => GetStream(qwenResponse));

        // Mock tool registration
        var tool = new ToolRegistration
        {
            Metadata = new ToolMetadata
            {
                Id = "list_directory",
                Name = "list_directory",
                Description = "List files in a directory"
            },
            IsEnabled = true
        };

        mockToolRegistry.Setup(x => x.GetTools(null, null, null, true))
            .Returns(new[] { tool });

        // Mock tool execution
        mockToolExecutor.Setup(x => x.ExecuteAsync(
                "list_directory",
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new Andy.Tools.Core.ToolExecutionResult
            {
                IsSuccessful = true,
                Data = new { files = new[] { "file1.txt", "file2.txt" } },
                DurationMs = 100
            });

        // Create service
        var service = new AiConversationService(
            mockLlmClient.Object,
            mockToolRegistry.Object,
            mockToolExecutor.Object,
            feed,
            "You are a helpful assistant with tool access.",
            jsonRepair,
            null,
            "qwen-3-coder-480b",
            "cerebras");

        // Act
        var response = await service.ProcessMessageAsync("list the files");

        // Assert
        mockToolExecutor.Verify(x => x.ExecuteAsync(
            "list_directory",
            It.Is<Dictionary<string, object?>>(p =>
                p.ContainsKey("directory_path") &&
                p["directory_path"] != null &&
                p["directory_path"].ToString() == "."),
            It.IsAny<ToolExecutionContext?>()), Times.Once);

        // Verify context was updated
        var stats = service.GetContextStats();
        Assert.True(stats.ToolCallCount > 0);
    }

    [Fact]
    public void QwenResponseParser_ParsesToolCallCorrectly()
    {
        // Arrange
        var jsonRepair = new JsonRepairService();
        var accumulator = new StreamingToolCallAccumulator(jsonRepair, null);
        var parser = new QwenResponseParser(jsonRepair, accumulator, null);

        // The actual Qwen response format
        var qwenResponse = @"}
{
  ""tool_call"": {
    ""name"": ""list_directory"",
    ""arguments"": {
      ""directory_path"": "".""
    }
  }
}";

        // Act
        var result = parser.Parse(qwenResponse);

        // Assert
        Assert.Single(result.ToolCalls);
        Assert.Equal("list_directory", result.ToolCalls[0].ToolId);
        Assert.True(result.ToolCalls[0].Parameters.ContainsKey("directory_path"));
        Assert.Equal(".", result.ToolCalls[0].Parameters["directory_path"]?.ToString());

        // Text should be cleaned (no stray braces)
        Assert.Empty(result.TextContent);
    }

    [Fact]
    public void QwenResponseParser_CleansRawJsonOutput()
    {
        // Arrange
        var jsonRepair = new JsonRepairService();
        var accumulator = new StreamingToolCallAccumulator(jsonRepair, null);
        var parser = new QwenResponseParser(jsonRepair, accumulator, null);

        // The raw JSON output that Qwen sometimes returns
        var rawJsonOutput = @"""contents"": [
""."",
""assets"",
""docs"",
""src"",
""tests"",
"".gitignore"",
""agents.md"",
""Andy.Cli.sln"",
""Andy.Cli.sln.DotSettings.user"",
""claude.md"",
""global.json"",
""LICENSE"",
""newfile.txt"",
""README.md""
],
""recursive"": false,
""include_hidden"": false,
""sort_by"": null,
""sort_descending"": true,
""max_depth"": 1,
""include_details"": false";

        // Act
        var cleaned = parser.CleanResponseText(rawJsonOutput);

        // Assert
        Assert.Empty(cleaned); // Should remove all raw JSON
    }
}