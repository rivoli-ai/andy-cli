using System.Linq;
using System.Text.Json;
using Andy.Cli.Parsing;
using Andy.Cli.Parsing.Compiler;
using Andy.Cli.Parsing.Parsers;
using Andy.Cli.Parsing.Rendering;
using Andy.Cli.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Parsing;

public class MixedResponseParsingTests
{
    private readonly Mock<IJsonRepairService> _mockJsonRepair;
    private readonly Mock<ILogger<QwenParser>> _mockLogger;
    private readonly QwenParser _parser;
    private readonly AstRenderer _renderer;

    public MixedResponseParsingTests()
    {
        _mockJsonRepair = new Mock<IJsonRepairService>();
        _mockLogger = new Mock<ILogger<QwenParser>>();
        _parser = new QwenParser(_mockJsonRepair.Object, _mockLogger.Object);
        _renderer = new AstRenderer();
        
        // Setup JSON repair to return original JSON (assume it's valid)
        _mockJsonRepair.Setup(x => x.TryRepairJson(It.IsAny<string>(), out It.Ref<string>.IsAny))
            .Returns((string input, out string repaired) =>
            {
                repaired = input;
                return true;
            });
    }

    [Fact]
    public void Should_Parse_And_Render_Mixed_ToolCall_And_Text_Response()
    {
        // Arrange - Response with tool call followed by text
        var response = @"{""tool"":""list_directory"",""parameters"":{""path"":"".""}}

What would you like to know about this directory?";

        // Act - Parse the response
        var ast = _parser.Parse(response);
        
        // Assert - AST should contain both tool call and text
        Assert.NotNull(ast);
        
        var toolCalls = ast.Children.OfType<ToolCallNode>().ToList();
        Assert.Single(toolCalls);
        Assert.Equal("list_directory", toolCalls[0].ToolName);
        
        var textNodes = ast.Children.OfType<TextNode>().ToList();
        Assert.NotEmpty(textNodes);
        var combinedText = string.Join(" ", textNodes.Select(t => t.Content)).Trim();
        Assert.Contains("What would you like to know about this directory?", combinedText);
        
        // Act - Render for streaming
        var renderResult = _renderer.RenderForStreaming(ast);
        
        // Assert - Should have both tool calls and text content
        Assert.True(renderResult.HasToolCalls);
        Assert.Single(renderResult.ToolCalls);
        Assert.Equal("list_directory", renderResult.ToolCalls[0].ToolId);
        
        Assert.True(renderResult.HasContent);
        Assert.NotNull(renderResult.TextContent);
        Assert.Contains("What would you like to know about this directory?", renderResult.TextContent);
    }

    [Fact]
    public void Should_Parse_Text_Before_And_After_ToolCall()
    {
        // Arrange - Response with text, then tool call, then more text
        var response = @"Let me check the directory structure for you.

{""tool"":""list_directory"",""parameters"":{""path"":"".""}}

I'll analyze the results and provide you with a summary.";

        // Act - Parse the response
        var ast = _parser.Parse(response);
        
        // Assert - AST should contain tool call and both text segments
        var toolCalls = ast.Children.OfType<ToolCallNode>().ToList();
        Assert.Single(toolCalls);
        
        var textNodes = ast.Children.OfType<TextNode>().ToList();
        Assert.NotEmpty(textNodes);
        var combinedText = string.Join(" ", textNodes.Select(t => t.Content)).Trim();
        Assert.Contains("Let me check the directory structure", combinedText);
        Assert.Contains("analyze the results", combinedText);
        
        // Act - Render for streaming
        var renderResult = _renderer.RenderForStreaming(ast);
        
        // Assert - Should have both tool calls and text content
        Assert.True(renderResult.HasToolCalls);
        Assert.True(renderResult.HasContent);
        Assert.Contains("Let me check", renderResult.TextContent);
        Assert.Contains("analyze the results", renderResult.TextContent);
    }

    [Fact]
    public void Should_Handle_Multiple_ToolCalls_With_Interspersed_Text()
    {
        // Arrange
        var response = @"I'll help you with multiple operations.

{""tool"":""read_file"",""parameters"":{""path"":""README.md""}}

Now let me check the configuration:

{""tool"":""read_file"",""parameters"":{""path"":""config.json""}}

Based on these files, here's what I found...";

        // Act - Parse
        var ast = _parser.Parse(response);
        
        // Assert
        var toolCalls = ast.Children.OfType<ToolCallNode>().ToList();
        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("read_file", toolCalls[0].ToolName);
        Assert.Equal("read_file", toolCalls[1].ToolName);
        
        var textNodes = ast.Children.OfType<TextNode>().ToList();
        var combinedText = string.Join(" ", textNodes.Select(t => t.Content)).Trim();
        Assert.Contains("help you with multiple operations", combinedText);
        Assert.Contains("check the configuration", combinedText);
        Assert.Contains("Based on these files", combinedText);
        
        // Render and verify
        var renderResult = _renderer.RenderForStreaming(ast);
        Assert.Equal(2, renderResult.ToolCalls.Count);
        Assert.True(renderResult.HasContent);
        Assert.Contains("multiple operations", renderResult.TextContent);
        Assert.Contains("Based on these files", renderResult.TextContent);
    }

    [Fact]
    public void Should_Not_Lose_Text_When_ToolCall_Is_Only_JSON()
    {
        // Arrange - Common case where LLM returns just tool JSON with no explanation
        var response = @"{""tool"":""git_status"",""parameters"":{}}";

        // Act
        var ast = _parser.Parse(response);
        var renderResult = _renderer.RenderForStreaming(ast);
        
        // Assert
        Assert.Single(ast.Children.OfType<ToolCallNode>());
        Assert.True(renderResult.HasToolCalls);
        
        // There should be no text content when response is only a tool call
        var textNodes = ast.Children.OfType<TextNode>().ToList();
        if (textNodes.Any())
        {
            // Text nodes should be empty or whitespace only
            Assert.All(textNodes, node => Assert.True(string.IsNullOrWhiteSpace(node.Content)));
        }
        Assert.False(renderResult.HasContent || !string.IsNullOrWhiteSpace(renderResult.TextContent));
    }

    [Fact] 
    public void Should_Extract_Text_After_JSON_In_Code_Block()
    {
        // Arrange - Tool call in code block followed by text (should not parse as tool)
        var response = @"Here's an example of a tool call:

```json
{""tool"":""example_tool"",""parameters"":{""test"":true}}
```

This is just an example, not an actual tool call.";

        // Act
        var ast = _parser.Parse(response);
        var renderResult = _renderer.RenderForStreaming(ast);
        
        // Assert - Should NOT have tool calls (it's in a code block)
        Assert.False(renderResult.HasToolCalls);
        Assert.True(renderResult.HasContent);
        Assert.Contains("example of a tool call", renderResult.TextContent);
        Assert.Contains("This is just an example", renderResult.TextContent);
    }
}