using Andy.Cli.Services;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.Cli.Tests.Services;

public class QwenResponseParserTests
{
    private readonly JsonRepairService _jsonRepair = new();
    private readonly StreamingToolCallAccumulator _accumulator;
    private readonly QwenResponseParser _parser;

    public QwenResponseParserTests()
    {
        _accumulator = new StreamingToolCallAccumulator(_jsonRepair, NullLogger<StreamingToolCallAccumulator>.Instance);
        _parser = new QwenResponseParser(_jsonRepair, _accumulator, NullLogger<QwenResponseParser>.Instance);
    }

    [Fact]
    public void Parse_SimpleResponse_ReturnsTextOnly()
    {
        // Arrange
        var response = "Hello! I can help you with that task.";

        // Act
        var result = _parser.Parse(response);

        // Assert
        Assert.Equal(response, result.TextContent);
        Assert.Empty(result.ToolCalls);
        Assert.False(result.HasErrors);
        Assert.True(result.Metadata.IsComplete);
    }

    [Fact]
    public void Parse_ResponseWithToolCallTags_ExtractsToolCall()
    {
        // Arrange
        var response = """
            I'll help you with that.
            <tool_call>
            {"name": "search_files", "arguments": {"query": "test", "path": "/home"}}
            </tool_call>
            """;

        // Act
        var result = _parser.Parse(response);

        // Assert
        Assert.True(result.HasToolCalls);
        Assert.Single(result.ToolCalls);
        
        var toolCall = result.ToolCalls[0];
        Assert.Equal("search_files", toolCall.ToolId);
        Assert.Equal("test", toolCall.Parameters["query"]?.ToString());
        Assert.Equal("/home", toolCall.Parameters["path"]?.ToString());
    }

    [Fact]
    public void Parse_ResponseWithMalformedJson_RepairsAndExtracts()
    {
        // Arrange
        var response = """
            <tool_call>
            {name: "test_tool", arguments: {param: "value", count: 123,}}
            </tool_call>
            """;

        // Act
        var result = _parser.Parse(response);

        // Assert
        Assert.True(result.HasToolCalls);
        Assert.Single(result.ToolCalls);
        
        var toolCall = result.ToolCalls[0];
        Assert.Equal("test_tool", toolCall.ToolId);
        Assert.Equal("value", toolCall.Parameters["param"]?.ToString());
        // Handle JsonElement for numeric values
        if (toolCall.Parameters["count"] is System.Text.Json.JsonElement element)
        {
            Assert.Equal(123, element.GetInt32());
        }
        else
        {
            Assert.Equal(123L, Convert.ToInt64(toolCall.Parameters["count"]));
        }
    }

    [Fact]
    public void Parse_ResponseWithJsonBlock_ExtractsToolCall()
    {
        // Arrange
        var response = """
            I need to use a tool for this:
            
            ```json
            {"name": "file_read", "arguments": {"path": "/etc/hosts"}}
            ```
            
            Let me execute that.
            """;

        // Act
        var result = _parser.Parse(response);

        // Assert
        Assert.True(result.HasToolCalls);
        Assert.Single(result.ToolCalls);
        Assert.Equal("file_read", result.ToolCalls[0].ToolId);
        Assert.Equal("/etc/hosts", result.ToolCalls[0].Parameters["path"]?.ToString());
    }

    [Fact]
    public void Parse_ResponseWithDirectJson_ExtractsToolCall()
    {
        // Arrange
        var response = """I'll call {"name": "get_weather", "arguments": {"city": "London"}} to get the weather.""";

        // Act
        var result = _parser.Parse(response);

        // Assert
        Assert.True(result.HasToolCalls);
        Assert.Single(result.ToolCalls);
        Assert.Equal("get_weather", result.ToolCalls[0].ToolId);
        Assert.Equal("London", result.ToolCalls[0].Parameters["city"]?.ToString());
    }

    [Fact]
    public void CleanResponseText_RemovesToolCallTags()
    {
        // Arrange
        var response = """
            I'll help you with that.
            <tool_call>
            {"name": "search_files", "arguments": {"query": "test"}}
            </tool_call>
            The search has been completed.
            """;

        // Act
        var cleaned = _parser.CleanResponseText(response);

        // Assert
        Assert.DoesNotContain("<tool_call>", cleaned);
        Assert.DoesNotContain("</tool_call>", cleaned);
        Assert.DoesNotContain("search_files", cleaned);
        Assert.Contains("I'll help you with that", cleaned);
        Assert.Contains("The search has been completed", cleaned);
    }

    [Fact]
    public void CleanResponseText_RemovesThinkingTags()
    {
        // Arrange
        var response = """
            <thinking>
            I need to figure out what tool to use here...
            </thinking>
            I'll help you with that task.
            """;

        // Act
        var cleaned = _parser.CleanResponseText(response);

        // Assert
        Assert.DoesNotContain("<thinking>", cleaned);
        Assert.DoesNotContain("</thinking>", cleaned);
        Assert.DoesNotContain("I need to figure out", cleaned);
        Assert.Contains("I'll help you with that task", cleaned);
    }

    [Fact]
    public void CleanResponseText_RemovesInternalThoughts()
    {
        // Arrange
        var response = "I'll use the search tool for this. Let me help you with that task.";

        // Act
        var cleaned = _parser.CleanResponseText(response);

        // Assert
        Assert.DoesNotContain("I'll use the search tool", cleaned);
        Assert.Contains("Let me help you with that task", cleaned);
    }

    [Fact]
    public void Parse_EmptyResponse_ReturnsError()
    {
        // Arrange
        var response = "";

        // Act
        var result = _parser.Parse(response);

        // Assert
        Assert.True(result.HasErrors);
        Assert.Single(result.Errors);
        Assert.Equal(ParseErrorType.IncompleteResponse, result.Errors[0].Type);
    }

    [Fact]
    public void ExtractToolCalls_MultipleFormats_ExtractsPrioritized()
    {
        // Arrange - Tool call tags should take priority over other formats
        var response = """
            <tool_call>
            {"name": "priority_tool", "arguments": {"important": true}}
            </tool_call>
            
            ```json
            {"name": "secondary_tool", "arguments": {"backup": true}}
            ```
            """;

        // Act
        var toolCalls = _parser.ExtractToolCalls(response);

        // Assert
        Assert.Single(toolCalls); // Should only extract the priority one
        Assert.Equal("priority_tool", toolCalls[0].ToolId);
        Assert.True(Convert.ToBoolean(toolCalls[0].Parameters["important"]));
    }

    [Fact]
    public void Parse_InvalidJsonInToolCall_AddsError()
    {
        // Arrange
        var response = """
            <tool_call>
            {this is completely invalid json}
            </tool_call>
            """;

        // Act
        var result = _parser.Parse(response);

        // Assert
        Assert.False(result.HasToolCalls);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Type == ParseErrorType.InvalidJson);
    }
}