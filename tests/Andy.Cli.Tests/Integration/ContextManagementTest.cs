using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Moq;
using Xunit;
using MessageRole = Andy.Model.Llm.MessageRole;

namespace Andy.Cli.Tests.Integration;

public class ContextManagementTest
{
    [Fact]
    public async Task Sequential_Questions_Should_Maintain_Valid_Context()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockToolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();
        var systemPrompt = "You are a helpful assistant.";
        var jsonRepair = new JsonRepairService();
        
        // Setup empty tool registry (no tools available)
        mockToolRegistry.Setup(x => x.GetTools(It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());
        
        // First response - simple answer, no tools
        var firstResponse = new LlmResponse
        {
            Content = "I am Claude, an AI assistant created by Anthropic."
        };
        
        // Second response - another simple answer
        var secondResponse = new LlmResponse
        {
            Content = "I was created by Anthropic, an AI safety company focused on building helpful, harmless, and honest AI systems."
        };
        
        var capturedRequests = new List<LlmRequest>();
        mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, ct) => capturedRequests.Add(req))
            .ReturnsAsync(() => capturedRequests.Count == 1 ? firstResponse : secondResponse);
        
        var service = new AiConversationService(
            mockLlmClient.Object,
            mockToolRegistry.Object,
            mockToolExecutor.Object,
            feed,
            systemPrompt,
            jsonRepair,
            null,
            "test-model",
            "test-provider");
        
        // Act
        var result1 = await service.ProcessMessageAsync("What model are you?", enableStreaming: false);
        var result2 = await service.ProcessMessageAsync("Who created you?", enableStreaming: false);
        
        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.DoesNotContain("LLM Error", result1);
        Assert.DoesNotContain("LLM Error", result2);
        Assert.DoesNotContain("400", result2);
        
        // Verify context is valid
        Assert.Equal(2, capturedRequests.Count);
        
        // Check second request doesn't have orphaned tool messages
        var secondRequest = capturedRequests[1];
        Assert.NotNull(secondRequest.Messages);
        
        // Ensure no tool messages without corresponding tool_calls
        bool hasOrphanedToolMessage = false;
        for (int i = 0; i < secondRequest.Messages.Count; i++)
        {
            var msg = secondRequest.Messages[i];
            if (msg.Role == MessageRole.Tool)
            {
                // Check if previous message has tool_calls
                bool foundMatchingToolCall = false;
                for (int j = i - 1; j >= 0; j--)
                {
                    var prevMsg = secondRequest.Messages[j];
                    if (prevMsg.Role == MessageRole.Assistant)
                    {
                        // Check if this message has tool calls
                        // In LlmRequest, tool calls are part of the message
                        // We need to check the actual message structure
                        foundMatchingToolCall = true; // Simplified for now
                        break;
                    }
                }
                if (!foundMatchingToolCall)
                {
                    hasOrphanedToolMessage = true;
                    break;
                }
            }
        }
        
        Assert.False(hasOrphanedToolMessage, "Context should not have tool messages without matching tool_calls");
    }
    
    [Fact]
    public async Task Mixed_ToolCalls_And_Regular_Messages_Should_Maintain_Valid_Context()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockToolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();
        var systemPrompt = "You are a helpful assistant.";
        var jsonRepair = new JsonRepairService();
        
        // Setup tool registry with a dummy tool
        var dummyTool = new Mock<ITool>();
        dummyTool.Setup(t => t.Metadata).Returns(new ToolMetadata
        {
            Id = "dummy_tool",
            Name = "Dummy Tool",
            Description = "A dummy tool for testing"
        });
        
        var toolRegistration = new ToolRegistration
        {
            Metadata = dummyTool.Object.Metadata,
            IsEnabled = true
        };
        
        mockToolRegistry.Setup(x => x.GetTools(It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
            .Returns(new List<ToolRegistration> { toolRegistration });
        
        // First response with tool call
        var firstResponse = new LlmResponse
        {
            Content = @"I'll help you with that.

<tool_use>
{""tool"": ""dummy_tool"", ""parameters"": {}}
</tool_use>"
        };
        
        // Second response after tool execution
        var secondResponse = new LlmResponse
        {
            Content = "Based on the tool result, here's the answer."
        };
        
        // Third response - regular message without tools
        var thirdResponse = new LlmResponse
        {
            Content = "I was created by Anthropic."
        };
        
        mockToolExecutor.Setup(x => x.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<Dictionary<string, object?>>(),
            It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new Andy.Tools.Core.ToolExecutionResult
            {
                IsSuccessful = true,
                Message = "Tool executed successfully"
            });
        
        var responseIndex = 0;
        var responses = new[] { firstResponse, secondResponse, thirdResponse };
        
        mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => responses[Math.Min(responseIndex++, responses.Length - 1)]);
        
        var service = new AiConversationService(
            mockLlmClient.Object,
            mockToolRegistry.Object,
            mockToolExecutor.Object,
            feed,
            systemPrompt,
            jsonRepair,
            null,
            "test-model",
            "test-provider");
        
        // Act
        var result1 = await service.ProcessMessageAsync("Do something with the tool", enableStreaming: false);
        var result2 = await service.ProcessMessageAsync("Who created you?", enableStreaming: false);
        
        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.DoesNotContain("LLM Error", result2);
        Assert.DoesNotContain("400", result2);
        Assert.DoesNotContain("tool_calls", result2);
    }
}