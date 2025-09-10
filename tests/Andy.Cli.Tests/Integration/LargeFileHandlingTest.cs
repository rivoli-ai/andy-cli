using System;
using System.Collections.Generic;
using System.IO;
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
using Andy.Tools.Library;
using Moq;
using Xunit;
using MessageRole = Andy.Llm.Models.MessageRole;

namespace Andy.Cli.Tests.Integration;

public class LargeFileHandlingTest
{
    [Fact]
    public async Task ProcessMessageAsync_WithLargeFileRead_HandlesCorrectly()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockToolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();
        var systemPrompt = "You are a helpful assistant.";
        var jsonRepair = new JsonRepairService();
        
        // Create a large file content (70KB+)
        var largeContent = new StringBuilder();
        largeContent.AppendLine("using System;");
        largeContent.AppendLine("using System.Collections.Generic;");
        // Add many lines to simulate Program.cs
        for (int i = 0; i < 2000; i++)
        {
            largeContent.AppendLine($"// This is line {i} of a very large file that simulates Program.cs content");
            largeContent.AppendLine($"public class TestClass{i} {{ public void Method() {{ Console.WriteLine(\"Test {i}\"); }} }}");
        }
        var fileContent = largeContent.ToString();
        Assert.True(fileContent.Length > 70000, "Test file should be > 70KB");
        
        // Setup tool registry to return read_file tool
        var readFileTool = new Mock<ITool>();
        readFileTool.Setup(t => t.Metadata).Returns(new ToolMetadata
        {
            Id = "read_file",
            Name = "Read File",
            Description = "Reads a file from the filesystem"
        });
        
        var toolRegistration = new ToolRegistration
        {
            Metadata = readFileTool.Object.Metadata,
            IsEnabled = true
        };
        
        mockToolRegistry.Setup(x => x.GetTools(It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
            .Returns(new List<ToolRegistration> { toolRegistration });
        mockToolRegistry.Setup(x => x.GetTool("read_file"))
            .Returns(toolRegistration);
        
        // Setup tool executor to return the large file content
        mockToolExecutor.Setup(x => x.ExecuteAsync(
            It.Is<string>(id => id == "read_file"),
            It.IsAny<Dictionary<string, object?>>(),
            It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new Andy.Tools.Core.ToolExecutionResult
            {
                IsSuccessful = true,
                Data = new Dictionary<string, object?>
                {
                    ["content"] = fileContent,
                    ["file_path"] = "./src/Andy.Cli/Program.cs",
                    ["size"] = fileContent.Length
                }
            });
        
        // First response - just text, no tool calls (simplified)
        var firstResponse = new LlmResponse
        {
            Content = "The Program.cs file appears to be quite large based on the repository structure. It contains the main entry point for the Andy CLI application."
        };
        
        mockLlmClient
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstResponse);
        
        var service = new AiConversationService(
            mockLlmClient.Object,
            mockToolRegistry.Object,
            mockToolExecutor.Object,
            feed,
            systemPrompt,
            jsonRepair,
            null,  // logger
            "test-model",
            "test-provider");
        
        // Act
        var result = await service.ProcessMessageAsync("Can you inspect the current repository and tell me about Program.cs?", enableStreaming: false);
        
        // Assert
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result), $"Result should not be empty. Result was: '{result}'");
        Assert.Contains("Program.cs", result);
        Assert.DoesNotContain("LLM Error", result); // Should not have errors
        
        // Verify LLM was called at least once
        mockLlmClient.Verify(x => x.CompleteAsync(
            It.IsAny<LlmRequest>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        
        // Check that the context was properly managed (tool result should be truncated)
        var contextStats = service.GetContextStats();
        Assert.True(contextStats.EstimatedTokens < 10000, "Context should not explode with large file content");
    }
    
    [Fact]
    public void ContextManager_TruncatesLargeToolResults()
    {
        // Arrange
        var contextManager = new ContextManager("System prompt", maxTokens: 6000);
        var largeResult = new string('x', 10000); // 10KB result
        
        // Act
        contextManager.AddToolExecution(
            "read_file",
            "call_123",
            new Dictionary<string, object?> { ["file_path"] = "test.txt" },
            largeResult);
        
        var context = contextManager.GetContext();
        
        // Assert
        Assert.NotNull(context);
        Assert.True(context.Messages.Count > 0);
        
        // Check that the stats show reasonable token count
        var stats = contextManager.GetStats();
        Assert.True(stats.EstimatedTokens < 1000, "Tool result should be truncated leading to reasonable token count");
    }
    
    [Fact]
    public void ContextManager_EstimatesTokensCorrectly()
    {
        // Arrange
        var contextManager = new ContextManager("System prompt");
        var testContent = "This is a test message with some content.";
        
        // Act
        contextManager.AddUserMessage(testContent);
        var stats = contextManager.GetStats();
        
        // Assert
        Assert.True(stats.EstimatedTokens > 0);
        Assert.True(stats.EstimatedTokens < 100, "Simple message should have reasonable token count");
    }
}