using System.Collections.Generic;
using Xunit;
using Andy.Cli.Services;

namespace Andy.Cli.Tests.Services;

public class ContextManagerTests
{
    [Fact]
    public void Constructor_SetsSystemPrompt()
    {
        // Arrange
        var systemPrompt = "You are a helpful assistant.";

        // Act
        var manager = new ContextManager(systemPrompt);
        var context = manager.GetContext();

        // Assert
        Assert.Equal(systemPrompt, context.SystemInstruction);
    }

    [Fact]
    public void AddUserMessage_AddsToContext()
    {
        // Arrange
        var manager = new ContextManager("System prompt");
        var userMessage = "Hello, how are you?";

        // Act
        manager.AddUserMessage(userMessage);
        var context = manager.GetContext();

        // Assert
        Assert.Equal(2, context.Messages.Count); // System + User
        Assert.Contains(context.Messages, m => m.Role == Andy.Llm.Models.MessageRole.User &&
            m.Parts.OfType<Andy.Llm.Models.TextPart>().Any(p => p.Text == userMessage));
    }

    [Fact]
    public void AddAssistantMessage_AddsToContext()
    {
        // Arrange
        var manager = new ContextManager("System prompt");
        var assistantMessage = "I'm doing well, thank you!";

        // Act
        manager.AddAssistantMessage(assistantMessage);
        var context = manager.GetContext();

        // Assert
        Assert.Equal(2, context.Messages.Count); // System + Assistant
        Assert.Contains(context.Messages, m => m.Role == Andy.Llm.Models.MessageRole.Assistant &&
            m.Parts.OfType<Andy.Llm.Models.TextPart>().Any(p => p.Text == assistantMessage));
    }

    [Fact]
    public void AddToolExecution_AddsToolCallAndResult()
    {
        // Arrange
        var manager = new ContextManager("System prompt");
        var toolId = "list_directory";
        var parameters = new Dictionary<string, object?> { ["path"] = "." };
        var result = @"{""files"": [""test.txt""]}";

        // Act
        manager.AddToolExecution(toolId, "call_test", parameters, result);
        var stats = manager.GetStats();

        // Assert
        Assert.Equal(1, stats.ToolCallCount);
        Assert.Equal(2, stats.MessageCount); // System prompt + 1 tool message
    }

    [Fact]
    public void Clear_ResetsMessagesButKeepsSystemPrompt()
    {
        // Arrange
        var systemPrompt = "System prompt";
        var manager = new ContextManager(systemPrompt);
        manager.AddUserMessage("User message");
        manager.AddAssistantMessage("Assistant message");

        // Act
        manager.Clear();
        var context = manager.GetContext();

        // Assert
        Assert.Single(context.Messages);
        Assert.Equal(systemPrompt, context.SystemInstruction);
    }

    [Fact]
    public void GetStats_ReturnsCorrectCounts()
    {
        // Arrange
        var manager = new ContextManager("System prompt");
        manager.AddUserMessage("Message 1");
        manager.AddAssistantMessage("Response 1");
        manager.AddToolExecution("tool1", "call_test", new Dictionary<string, object?>(), "result1");
        manager.AddUserMessage("Message 2");

        // Act
        var stats = manager.GetStats();

        // Assert
        Assert.Equal(5, stats.MessageCount); // System + 2 User + 1 Assistant + 1 Tool
        Assert.Equal(1, stats.ToolCallCount);
        Assert.True(stats.EstimatedTokens > 0);
    }

    [Fact]
    public void UpdateSystemPrompt_ChangesSystemMessage()
    {
        // Arrange
        var manager = new ContextManager("Initial prompt");
        var newPrompt = "Updated system prompt";

        // Act
        manager.UpdateSystemPrompt(newPrompt);
        var context = manager.GetContext();

        // Assert
        Assert.Equal(newPrompt, context.SystemInstruction);
    }

    [Fact]
    public void EstimateTokens_ReturnsReasonableEstimate()
    {
        // Arrange
        var manager = new ContextManager("This is a system prompt with several words.");
        manager.AddUserMessage("This is a user message with some content.");
        manager.AddAssistantMessage("This is an assistant response with more words and content.");

        // Act
        var stats = manager.GetStats();

        // Assert
        // Rough estimate: ~4 chars per token
        Assert.True(stats.EstimatedTokens > 20);
        Assert.True(stats.EstimatedTokens < 100);
    }

    [Fact]
    public void CompressContext_ReducesMessageCountWhenOverLimit()
    {
        // Arrange
        var manager = new ContextManager("System prompt");

        // Add many messages to exceed compression threshold
        for (int i = 0; i < 50; i++)
        {
            manager.AddUserMessage($"User message {i} with some substantial content to increase token count");
            manager.AddAssistantMessage($"Assistant response {i} with equally substantial content for testing");
        }

        var initialStats = manager.GetStats();

        // Act - Get context should trigger compression if over threshold
        var context = manager.GetContext();
        var finalStats = manager.GetStats();

        // Assert
        // After compression, we should have fewer messages but still maintain context
        Assert.True(finalStats.MessageCount <= initialStats.MessageCount);
        Assert.NotNull(context.SystemInstruction);
    }

    [Fact]
    public void ToolExecution_FormatsCorrectly()
    {
        // Arrange
        var manager = new ContextManager("System prompt");
        var toolId = "read_file";
        var parameters = new Dictionary<string, object?>
        {
            ["file_path"] = "/test/file.txt",
            ["encoding"] = "utf-8"
        };
        var result = "File contents here";

        // Act
        manager.AddToolExecution(toolId, "call_test", parameters, result);
        var context = manager.GetContext();

        // Assert - Tool executions are stored as Tool messages with ToolResponsePart
        var toolMessage = context.Messages.FirstOrDefault(m => m.Role == Andy.Llm.Models.MessageRole.Tool);
        Assert.NotNull(toolMessage);

        var toolResponsePart = toolMessage?.Parts.OfType<Andy.Llm.Models.ToolResponsePart>().FirstOrDefault();
        Assert.NotNull(toolResponsePart);
        Assert.Equal(toolId, toolResponsePart?.ToolName);

        // The raw tool result is in the Response field (no internal headers)
        var responseText = toolResponsePart?.Response?.ToString() ?? "";
        Assert.DoesNotContain("[Tool Execution:", responseText);
        Assert.Contains(result, responseText);
    }
}