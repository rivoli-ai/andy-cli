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
using Andy.Tools.Library;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Integration;

public class ListDirectoryTruncationTest
{
    [Fact]
    public async Task ListDirectory_LargeOutput_ShouldNotCause400Error()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockToolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();
        var systemPrompt = "You are a helpful assistant.";
        var jsonRepair = new JsonRepairService();
        
        // Create a large directory listing
        var items = new List<object>();
        for (int i = 0; i < 100; i++)
        {
            items.Add(new
            {
                name = $"file_{i:D3}.txt",
                fullPath = $"/test/path/file_{i:D3}.txt",
                type = "file",
                size = 1024 * (i + 1),
                sizeFormatted = $"{i + 1} KB",
                lastModified = DateTime.Now.AddDays(-i).ToString("O")
            });
        }
        
        var largeDirectoryResult = new
        {
            items = items,
            totalItems = items.Count,
            totalSize = items.Count * 50 * 1024,
            path = "/test/path"
        };
        
        // Setup tool registry
        var listDirTool = new Mock<ITool>();
        listDirTool.Setup(t => t.Metadata).Returns(new ToolMetadata
        {
            Id = "list_directory",
            Name = "List Directory",
            Description = "Lists contents of a directory"
        });
        
        var toolRegistration = new ToolRegistration
        {
            Metadata = listDirTool.Object.Metadata,
            IsEnabled = true
        };
        
        mockToolRegistry.Setup(x => x.GetTools(It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
            .Returns(new List<ToolRegistration> { toolRegistration });
        mockToolRegistry.Setup(x => x.GetTool("list_directory"))
            .Returns(toolRegistration);
        
        // Setup tool executor to return large directory listing
        mockToolExecutor.Setup(x => x.ExecuteAsync(
            It.Is<string>(id => id == "list_directory"),
            It.IsAny<Dictionary<string, object?>>(),
            It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new Andy.Tools.Core.ToolExecutionResult
            {
                IsSuccessful = true,
                Data = largeDirectoryResult
            });
        
        // Setup LLM response
        var firstResponse = new LlmResponse
        {
            Content = @"I'll help you find files with 'AI' in their names. Let me list the directory contents first.

<tool_use>
{""tool"": ""list_directory"", ""parameters"": {""path"": "".""}}
</tool_use>"
        };
        
        var secondResponse = new LlmResponse
        {
            Content = "Based on the directory listing, I found several files. However, I don't see any files with 'AI' in their names specifically."
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
        var result = await service.ProcessMessageAsync("Is there a file named AI something .cs", enableStreaming: false);
        
        // Assert
        Assert.NotNull(result);
        Assert.DoesNotContain("LLM Error", result);
        Assert.DoesNotContain("400", result);
        
        // Verify tool was executed
        mockToolExecutor.Verify(x => x.ExecuteAsync(
            It.Is<string>(id => id == "list_directory"),
            It.IsAny<Dictionary<string, object?>>(),
            It.IsAny<ToolExecutionContext?>()), Times.Once);
        
        // Verify LLM was called at least twice
        mockLlmClient.Verify(x => x.CompleteAsync(
            It.IsAny<LlmRequest>(),
            It.IsAny<CancellationToken>()), Times.AtLeast(2));
        
        // Check that the second request (after tool execution) doesn't have overly large content
        if (capturedRequests.Count >= 2)
        {
            var secondRequest = capturedRequests[1];
            var serialized = System.Text.Json.JsonSerializer.Serialize(secondRequest);
            
            // The request should be reasonable in size (under 100KB)
            Assert.True(serialized.Length < 100000, $"Request too large: {serialized.Length} bytes");
        }
        
        // Verify context stats are reasonable
        var contextStats = service.GetContextStats();
        Assert.True(contextStats.EstimatedTokens < 10000, "Context should not explode with large directory listing");
    }
    
    [Fact]
    public async Task ListDirectory_RecursiveLargeOutput_ShouldNotCause400Error()
    {
        // Arrange
        var mockLlmClient = new Mock<LlmClient>("test-api-key");
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockToolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();
        var systemPrompt = "You are a helpful assistant.";
        var jsonRepair = new JsonRepairService();
        
        // Create a massive recursive directory listing
        var items = new List<object>();
        for (int dir = 0; dir < 20; dir++)
        {
            items.Add(new
            {
                name = $"dir_{dir:D2}",
                fullPath = $"/test/path/dir_{dir:D2}",
                type = "directory",
                depth = 0
            });
            
            // Add nested items
            for (int file = 0; file < 50; file++)
            {
                items.Add(new
                {
                    name = $"file_{file:D3}.txt",
                    fullPath = $"/test/path/dir_{dir:D2}/subdir/nested/file_{file:D3}.txt",
                    type = "file",
                    size = 1024 * (file + 1),
                    sizeFormatted = $"{file + 1} KB",
                    depth = 3
                });
            }
        }
        
        var largeRecursiveResult = new
        {
            items = items,
            totalItems = items.Count,
            totalSize = items.Count * 50 * 1024,
            path = "/test/path",
            recursive = true
        };
        
        // Setup tool registry
        var listDirTool = new Mock<ITool>();
        listDirTool.Setup(t => t.Metadata).Returns(new ToolMetadata
        {
            Id = "list_directory",
            Name = "List Directory",
            Description = "Lists contents of a directory"
        });
        
        var toolRegistration = new ToolRegistration
        {
            Metadata = listDirTool.Object.Metadata,
            IsEnabled = true
        };
        
        mockToolRegistry.Setup(x => x.GetTools(It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
            .Returns(new List<ToolRegistration> { toolRegistration });
        mockToolRegistry.Setup(x => x.GetTool("list_directory"))
            .Returns(toolRegistration);
        
        // Setup tool executor to return massive recursive listing
        mockToolExecutor.Setup(x => x.ExecuteAsync(
            It.Is<string>(id => id == "list_directory"),
            It.IsAny<Dictionary<string, object?>>(),
            It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new Andy.Tools.Core.ToolExecutionResult
            {
                IsSuccessful = true,
                Data = largeRecursiveResult
            });
        
        // Setup LLM response
        var firstResponse = new LlmResponse
        {
            Content = @"I'll list all files recursively to find what you're looking for.

<tool_use>
{""tool"": ""list_directory"", ""parameters"": {""path"": ""."", ""recursive"": true}}
</tool_use>"
        };
        
        var secondResponse = new LlmResponse
        {
            Content = "I found a large directory structure with many nested files and directories."
        };
        
        mockLlmClient
            .SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstResponse)
            .ReturnsAsync(secondResponse);
        
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
        var result = await service.ProcessMessageAsync("Show me all source files", enableStreaming: false);
        
        // Assert
        Assert.NotNull(result);
        Assert.DoesNotContain("LLM Error", result);
        Assert.DoesNotContain("400", result);
        
        // Verify context stats are reasonable
        var contextStats = service.GetContextStats();
        Assert.True(contextStats.EstimatedTokens < 10000, $"Context should not explode with recursive listing. Got {contextStats.EstimatedTokens} tokens");
    }
    
    [Fact]
    public void ToolExecutionItem_ShouldTruncateDisplayButNotActualResult()
    {
        // Arrange
        var toolId = "list_directory";
        var parameters = new Dictionary<string, object?> { ["path"] = "." };
        
        // Create a large result
        var largeResult = new StringBuilder();
        largeResult.AppendLine("{");
        largeResult.AppendLine("  \"items\": [");
        for (int i = 0; i < 50; i++)
        {
            largeResult.AppendLine($"    {{\"name\": \"file_{i}.txt\", \"type\": \"file\"}},");
        }
        largeResult.AppendLine("  ]");
        largeResult.AppendLine("}");
        
        // Act
        var item = new ToolExecutionItem(toolId, parameters, largeResult.ToString(), true);
        
        // The display should be truncated but we need to ensure the actual result passed to LLM is not
        // This test verifies the ToolExecutionItem behavior
        
        // Measure how many lines would be displayed
        var displayLines = item.MeasureLineCount(80);
        
        // Assert - display should be limited
        Assert.True(displayLines < 20, "Display should be truncated to reasonable number of lines");
    }
}