using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class ModelResponseInterpreterTests
{
    private readonly ModelResponseInterpreter _interpreter;

    public ModelResponseInterpreterTests()
    {
        _interpreter = new ModelResponseInterpreter();
    }

    [Fact]
    public void QwenModel_ExtractsToolCall_FromToolCallTags()
    {
        // Arrange
        var response = @"I'll list the files in the current directory for you.

<tool_call>
{""name"":""list_directory"",""arguments"":{""path"":"".""}}
</tool_call>";

        // Act
        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");

        // Assert
        Assert.Single(toolCalls);
        var toolCall = toolCalls.First();
        Assert.Equal("list_directory", toolCall.ToolId);
        Assert.True(toolCall.Parameters.ContainsKey("path"));
        Assert.Equal(".", toolCall.Parameters["path"]);
    }

    [Fact]
    public void QwenModel_CleansResponse_RemovesToolCallTags()
    {
        // Arrange
        var response = @"I'll list the files for you.

<tool_call>
{""name"":""list_directory"",""arguments"":{""path"":"".""}}
</tool_call>

Here are the results:";

        // Act
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");

        // Assert
        Assert.DoesNotContain("<tool_call>", cleaned);
        Assert.DoesNotContain("</tool_call>", cleaned);
        Assert.Contains("I'll list the files for you.", cleaned);
        Assert.Contains("Here are the results:", cleaned);
    }

    [Fact]
    public void QwenModel_FormatsToolResults_Correctly()
    {
        // Arrange
        var toolCalls = new List<ModelToolCall>
        {
            new ModelToolCall { ToolId = "list_directory", Parameters = new Dictionary<string, object?> { ["path"] = "." } }
        };
        var results = new List<string> { "file1.txt\nfile2.txt\ndir1/" };

        // Act
        var formatted = _interpreter.FormatToolResults(toolCalls, results, "qwen-3-coder-480b", "cerebras");

        // Assert
        // Qwen now uses simplified format
        Assert.Contains("[list_directory result]", formatted);
        Assert.Contains("file1.txt", formatted);
    }

    [Fact]
    public void LlamaModel_ExtractsToolCall_FromJsonBlock()
    {
        // Arrange
        var response = @"I'll help you with that.

```json
{""tool"":""read_file"",""parameters"":{""file_path"":""/etc/hosts""}}
```";

        // Act
        var toolCalls = _interpreter.ExtractToolCalls(response, "llama-3.1-70b", "cerebras");

        // Assert
        Assert.Single(toolCalls);
        var toolCall = toolCalls.First();
        Assert.Equal("read_file", toolCall.ToolId);
        Assert.True(toolCall.Parameters.ContainsKey("file_path"));
        Assert.Equal("/etc/hosts", toolCall.Parameters["file_path"]);
    }

    [Fact]
    public void ClaudeModel_ExtractsToolCall_FromToolUseTags()
    {
        // Arrange
        var response = @"I'll read that file for you.

<tool_use>
{""tool"":""read_file"",""parameters"":{""file_path"":""/etc/hosts""}}
</tool_use>";

        // Act
        var toolCalls = _interpreter.ExtractToolCalls(response, "claude-3-sonnet", "anthropic");

        // Assert
        Assert.Single(toolCalls);
        var toolCall = toolCalls.First();
        Assert.Equal("read_file", toolCall.ToolId);
        Assert.True(toolCall.Parameters.ContainsKey("file_path"));
        Assert.Equal("/etc/hosts", toolCall.Parameters["file_path"]);
    }

    [Fact]
    public void ContainsFakeToolResults_DetectsFakeResults()
    {
        // Arrange
        var fakeResponse1 = "[Tool Results]\n{\"files\": [\"file1.txt\"]}";
        var fakeResponse2 = "Tool execution result:\n{\"output\": \"fake\"}";
        var realResponse = "Here are the actual results from the tool.";

        // Act & Assert
        Assert.True(_interpreter.ContainsFakeToolResults(fakeResponse1, "qwen-3-coder"));
        Assert.True(_interpreter.ContainsFakeToolResults(fakeResponse2, "llama"));
        Assert.False(_interpreter.ContainsFakeToolResults(realResponse, "gpt-4"));
    }

    [Fact]
    public void O1Model_RemovesThinkingTags()
    {
        // Arrange
        var response = @"<thinking>
Let me analyze this problem step by step.
First, I need to understand what the user wants.
</thinking>

I'll help you with that task.";

        // Act
        var cleaned = _interpreter.CleanResponseForDisplay(response, "o1-preview");

        // Assert
        Assert.DoesNotContain("<thinking>", cleaned);
        Assert.DoesNotContain("</thinking>", cleaned);
        Assert.DoesNotContain("Let me analyze", cleaned);
        Assert.Contains("I'll help you with that task.", cleaned);
    }
}