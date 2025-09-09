using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Integration;

public class AiConversationComprehensiveAnswerTest
{
    [Fact]
    public async Task AskingForServiceContents_ProducesComprehensiveMultiStepAnswer()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>((string)"test-key");
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockToolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();

        var systemPrompt = "You are a helpful assistant with tool access.";
        var jsonRepair = new JsonRepairService();

        // First LLM turn: ask to list repo root and read the AiConversationService.cs file
        var firstResponse = new LlmResponse
        {
            Content = "I will examine the service by listing the repo and reading the target file.\n{\"tool\": \"list_directory\", \"parameters\": {\"path\": \"./src/Andy.Cli/Services\"}}\n{\"tool\": \"read_file\", \"parameters\": {\"file_path\": \"./src/Andy.Cli/Services/AiConversationService.cs\"}}"
        };

        var concludingResponse = new LlmResponse
        {
            Content = "AiConversationService orchestrates LLM calls, executes tools (multi-call per turn), accumulates results, and renders responses. It exposes ProcessMessageAsync, UpdateModelInfo, ClearContext, GetContextStats, and sanitization routines."
        };

        mockLlmClient
            .SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstResponse)
            .ReturnsAsync(concludingResponse);

        // Tool registry lookups
        mockToolRegistry.Setup(x => x.GetTools(It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());
        mockToolRegistry.Setup(x => x.GetTool("list_directory"))
            .Returns(new ToolRegistration { Metadata = new ToolMetadata { Id = "list_directory", Name = "List Directory" }, IsEnabled = true });
        mockToolRegistry.Setup(x => x.GetTool("read_file"))
            .Returns(new ToolRegistration { Metadata = new ToolMetadata { Id = "read_file", Name = "Read File" }, IsEnabled = true });

        // Tool execution outputs
        mockToolExecutor
            .Setup(x => x.ExecuteAsync(
                It.Is<string>(id => id == "list_directory"),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new Andy.Tools.Core.ToolExecutionResult
            {
                IsSuccessful = true,
                Data = new
                {
                    items = new[]
                    {
                        new { name = "AiConversationService.cs", type = "file", depth = 0 },
                        new { name = "QwenResponseParser.cs", type = "file", depth = 0 }
                    }
                }
            });

        mockToolExecutor
            .Setup(x => x.ExecuteAsync(
                It.Is<string>(id => id == "read_file"),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new Andy.Tools.Core.ToolExecutionResult
            {
                IsSuccessful = true,
                Data = new
                {
                    path = "./src/Andy.Cli/Services/AiConversationService.cs",
                    snippet = "public class AiConversationService { public async Task<string> ProcessMessageAsync(string userMessage, bool enableStreaming, CancellationToken cancellationToken = default) { } }"
                }
            });

        var service = new AiConversationService(
            mockLlmClient.Object,
            mockToolRegistry.Object,
            mockToolExecutor.Object,
            feed,
            systemPrompt,
            jsonRepair,
            null,
            "qwen-3-coder",
            "cerebras");

        // Act
        var answer = await service.ProcessMessageAsync("What's inside AiConversationService?", enableStreaming: false);

        // Assert: final answer should be comprehensive (mention key methods and roles)
        Assert.Contains("AiConversationService", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ProcessMessageAsync", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("executes tools", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multi", answer, StringComparison.OrdinalIgnoreCase); // multi-call per turn hint
    }
}


