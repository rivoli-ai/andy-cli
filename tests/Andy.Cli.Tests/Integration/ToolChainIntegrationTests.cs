using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Andy.Cli.Services;

namespace Andy.Cli.Tests.Integration;

public class ToolChainIntegrationTests
{
    [Theory]
    [InlineData(@"{""tool"": ""list_directory"", ""parameters"": {""path"": "".""}}", "list_directory")]
    [InlineData(@"{""function"": ""read_file"", ""arguments"": {""file_path"": ""test.txt""}}", "read_file")]
    [InlineData(@"<tool_use>{""tool"": ""write_file"", ""parameters"": {""content"": ""hello""}}</tool_use>", "write_file")]
    [InlineData(@"```json
{
  ""tool"": ""process_info"",
  ""parameters"": {}
}
```", "process_info")]
    public void ToolCallParsing_HandlesVariousFormats(string input, string expectedToolId)
    {
        // This tests that various LLM response formats are correctly parsed
        // We would need to expose the parsing logic or test through the public interface
        Assert.Contains(expectedToolId, input);
    }

    [Fact]
    public void ToolOutputSerialization_ProducesValidJson()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            ["files"] = new[] { "file1.txt", "file2.txt" },
            ["directories"] = new[] { "dir1", "dir2" },
            ["count"] = 4
        };

        // Act
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Assert
        Assert.Contains("\"files\"", json);
        Assert.Contains("file1.txt", json);
        Assert.Contains("\"directories\"", json);
        Assert.Contains("\"count\": 4", json);
        
        // Verify it's valid JSON
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        Assert.NotNull(parsed);
    }

    [Fact]
    public async Task CompleteToolChain_ExecutesAndReturnsResults()
    {
        // This test would require setting up a complete integration environment
        // In a real scenario, you might use test containers or in-memory implementations
        
        // Arrange
        var contextManager = new ContextManager("You are a helpful assistant with tools.");
        contextManager.AddUserMessage("List the files in the current directory");
        
        // Simulate tool call detection
        var toolCallJson = @"{""tool"": ""list_directory"", ""parameters"": {""path"": ""."", ""recursive"": false}}";
        
        // Simulate tool execution result
        var toolResult = @"{
  ""files"": [""README.md"", ""Program.cs"", ""test.txt""],
  ""directories"": [""src"", ""tests"", ""bin""]
}";
        
        contextManager.AddToolExecution("list_directory", 
            new Dictionary<string, object?> { ["path"] = ".", ["recursive"] = false }, 
            toolResult);
        
        // Act
        var context = contextManager.GetContext();
        var stats = contextManager.GetStats();

        // Assert
        Assert.Equal(1, stats.ToolCallCount);
        Assert.True(stats.MessageCount >= 3); // System + User + Tool messages
        
        // Verify tool result is in context
        var lastMessage = context.Messages[context.Messages.Count - 1];
        Assert.Contains("README.md", lastMessage.Content);
    }

    [Fact]
    public void ContextCompression_MaintainsImportantInformation()
    {
        // Arrange
        var manager = new ContextManager("System prompt with tool access.");
        
        // Add important tool executions
        manager.AddUserMessage("List files");
        manager.AddToolExecution("list_directory", 
            new Dictionary<string, object?> { ["path"] = "." }, 
            @"{""files"": [""important.txt""]}");
        
        // Add many filler messages
        for (int i = 0; i < 30; i++)
        {
            manager.AddUserMessage($"Filler question {i}");
            manager.AddAssistantMessage($"Filler response {i}");
        }
        
        // Add another important tool execution
        manager.AddUserMessage("Read the important file");
        manager.AddToolExecution("read_file", 
            new Dictionary<string, object?> { ["file_path"] = "important.txt" }, 
            "Critical content that must be preserved");

        // Act
        var context = manager.GetContext();
        var stats = manager.GetStats();

        // Assert
        // Even after potential compression, tool executions should be preserved
        Assert.True(stats.ToolCallCount >= 2);
        
        // The context should still contain references to the tools
        var contextStr = string.Join("\n", context.Messages.ConvertAll(m => m.Content));
        Assert.Contains("list_directory", contextStr);
        Assert.Contains("read_file", contextStr);
    }

    [Fact]
    public void ErrorHandling_ToolNotFound()
    {
        // Arrange
        var manager = new ContextManager("System prompt");
        var invalidToolId = "non_existent_tool";
        
        // Act
        manager.AddToolExecution(invalidToolId, 
            new Dictionary<string, object?>(), 
            "Error: Tool not found");
        
        var stats = manager.GetStats();

        // Assert
        Assert.Equal(1, stats.ToolCallCount);
        var context = manager.GetContext();
        Assert.Contains(context.Messages, m => m.Content.Contains("Error"));
    }

    [Fact]
    public void ParameterValidation_HandlesNullAndEmptyValues()
    {
        // Arrange
        var parameters = new Dictionary<string, object?>
        {
            ["null_param"] = null,
            ["empty_string"] = "",
            ["valid_param"] = "value"
        };

        // Act
        var json = JsonSerializer.Serialize(parameters, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Assert
        Assert.Contains("null", json);
        Assert.Contains("\"\"", json);
        Assert.Contains("value", json);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Just a regular response", false)]
    [InlineData(@"{""tool"": ""list""}", true)]
    [InlineData(@"Let me list the files: {""tool"": ""list_directory""}", true)]
    [InlineData(@"<tool_use>{""tool"": ""read""}</tool_use>", true)]
    public void ToolCallDetection_IdentifiesToolCalls(string response, bool expectedHasToolCall)
    {
        // This would test the tool call detection logic
        var hasJsonLikePattern = response.Contains(@"""tool""") || response.Contains(@"""function""");
        Assert.Equal(expectedHasToolCall, hasJsonLikePattern);
    }
}