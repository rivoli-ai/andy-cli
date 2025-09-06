using System.Linq;
using Andy.Cli.Services;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

public class QwenMissingToolCallTest
{
    private readonly ModelResponseInterpreter _interpreter;
    private readonly ITestOutputHelper _output;

    public QwenMissingToolCallTest(ITestOutputHelper output)
    {
        _interpreter = new ModelResponseInterpreter();
        _output = output;
    }

    [Fact]
    public void QwenModel_IntentWithoutToolCall_ShouldDetectMissingTool()
    {
        // This is the case where Qwen says it will do something but doesn't output a tool call
        var response = @"Let's list the contents of the src directory.";
        
        // Check if any tool calls are extracted
        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");
        
        // This response indicates intent but has no tool call
        Assert.Empty(toolCalls);
        
        // The response suggests Qwen wants to use a tool but didn't
        Assert.Contains("list", response.ToLower());
        Assert.Contains("directory", response.ToLower());
    }

    [Fact]
    public void QwenModel_ShouldOutputToolCallForListDirectory()
    {
        // When asked about src/, Qwen should output a tool call
        var expectedResponse = @"Let's list the contents of the src directory.

{""tool"":""list_directory"",""parameters"":{""path"":""src""}}";
        
        var toolCalls = _interpreter.ExtractToolCalls(expectedResponse, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");
        
        // Should find the tool call
        Assert.Single(toolCalls);
        Assert.Equal("list_directory", toolCalls[0].ToolId);
        Assert.Equal("src", toolCalls[0].Parameters["path"]);
        
        // Clean the response
        var cleaned = _interpreter.CleanResponseForDisplay(expectedResponse, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned: '{cleaned}'");
        
        // Should not contain the JSON
        Assert.DoesNotContain("{\"tool\"", cleaned);
    }

    [Fact]
    public void QwenModel_ImpliedToolUse_WithoutExplicitCall()
    {
        // Test various cases where Qwen implies it will use a tool but doesn't
        var responses = new[]
        {
            "Let me check the system information for you.",
            "I'll read the README file now.",
            "Let's explore what's in the docs folder.",
            "I'm going to list the directory contents."
        };

        foreach (var response in responses)
        {
            var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
            _output.WriteLine($"Response: '{response}' - Tool calls: {toolCalls.Count}");
            
            // These responses suggest tool use but have no actual tool calls
            Assert.Empty(toolCalls);
        }
    }

    [Fact]
    public void QwenModel_ProperToolCall_AfterIntent()
    {
        // This is what Qwen SHOULD output
        var response = @"Let me check what's in the src directory for you.

{""tool"":""list_directory"",""parameters"":{""path"":""src""}}";

        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");
        
        Assert.Single(toolCalls);
        Assert.Equal("list_directory", toolCalls[0].ToolId);
        
        // Verify the path parameter
        Assert.True(toolCalls[0].Parameters.ContainsKey("path"));
        Assert.Equal("src", toolCalls[0].Parameters["path"]);
    }
}