using System.Linq;
using Andy.Cli.Services;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

public class QwenFullFlowTest
{
    private readonly ModelResponseInterpreter _interpreter;
    private readonly ITestOutputHelper _output;

    public QwenFullFlowTest(ITestOutputHelper output)
    {
        _interpreter = new ModelResponseInterpreter();
        _output = output;
    }

    [Fact]
    public void QwenModel_GenericFormatWithEmptyParams_ExtractsAndCleansCorrectly()
    {
        // Test the exact output the user reported: hello → {"tool":"system_info","parameters":{}}
        var response = @"Hello! I'd be happy to help you. Let me get some system information first.

{""tool"":""system_info"",""parameters"":{}}";

        // 1. Verify tool extraction works
        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");

        Assert.Single(toolCalls);
        Assert.Equal("system_info", toolCalls[0].ToolId);
        Assert.Empty(toolCalls[0].Parameters);

        // 2. Verify response cleaning removes the JSON
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned response: '{cleaned}'");

        Assert.DoesNotContain("{\"tool\"", cleaned);
        Assert.DoesNotContain("system_info", cleaned);
        Assert.Contains("Hello!", cleaned);
        Assert.Contains("system information", cleaned);
    }

    [Fact]
    public void QwenModel_GenericFormatWithParams_ExtractsAndCleansCorrectly()
    {
        // Test with parameters present
        var response = @"I'll list the contents of the current directory for you.

{""tool"":""list_directory"",""parameters"":{""path"":"".""}}

This will show all files and folders.";

        // 1. Verify tool extraction works
        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");

        Assert.Single(toolCalls);
        Assert.Equal("list_directory", toolCalls[0].ToolId);
        Assert.Single(toolCalls[0].Parameters);
        Assert.Equal(".", toolCalls[0].Parameters["path"]);

        // 2. Verify response cleaning removes the JSON
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned response: '{cleaned}'");

        Assert.DoesNotContain("{\"tool\"", cleaned);
        Assert.DoesNotContain("list_directory", cleaned);
        Assert.Contains("I'll list", cleaned);
        Assert.Contains("This will show", cleaned);
    }

    [Fact]
    public void QwenModel_PreferredToolCallFormat_ExtractsAndCleansCorrectly()
    {
        // Test the preferred <tool_call> format
        var response = @"Let me search for that information.

<tool_call>
{""name"":""search_text"",""arguments"":{""pattern"":""TODO"",""path"":""."",""method"":""regex""}}
</tool_call>

I'll search for TODO items in your code.";

        // 1. Verify tool extraction works
        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");

        Assert.Single(toolCalls);
        Assert.Equal("search_text", toolCalls[0].ToolId);
        Assert.Equal(3, toolCalls[0].Parameters.Count);
        Assert.Equal("TODO", toolCalls[0].Parameters["pattern"]);
        Assert.Equal(".", toolCalls[0].Parameters["path"]);
        Assert.Equal("regex", toolCalls[0].Parameters["method"]);

        // 2. Verify response cleaning removes the tool_call tags
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned response: '{cleaned}'");

        Assert.DoesNotContain("<tool_call>", cleaned);
        Assert.DoesNotContain("</tool_call>", cleaned);
        Assert.DoesNotContain("{\"name\"", cleaned);
        Assert.Contains("Let me search", cleaned);
        Assert.Contains("I'll search for TODO", cleaned);
    }

    [Fact]
    public void QwenModel_MultipleToolCalls_ExtractsAll()
    {
        // Test multiple tool calls in one response
        var response = @"I'll help you with multiple tasks.

First, let me get system information:
{""tool"":""system_info"",""parameters"":{}}

Then I'll list the directory:
{""tool"":""list_directory"",""parameters"":{""path"":"".""}}

That should give us what we need.";

        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");

        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("system_info", toolCalls[0].ToolId);
        Assert.Equal("list_directory", toolCalls[1].ToolId);

        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned response: '{cleaned}'");

        Assert.DoesNotContain("{\"tool\"", cleaned);
        Assert.Contains("multiple tasks", cleaned);
        Assert.Contains("That should give us", cleaned);
    }
}