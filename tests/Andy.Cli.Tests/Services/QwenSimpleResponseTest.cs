using System.Linq;
using Andy.Cli.Services;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

public class QwenSimpleResponseTest
{
    private readonly ModelResponseInterpreter _interpreter;
    private readonly ITestOutputHelper _output;

    public QwenSimpleResponseTest(ITestOutputHelper output)
    {
        _interpreter = new ModelResponseInterpreter();
        _output = output;
    }

    [Fact]
    public void QwenModel_SimpleHelloResponse_ShouldNotBeFake()
    {
        // This is what Qwen outputs for a simple "hello"
        var response = @"Hello! Welcome to the Andy CLI project. I'm here to help you with any questions or tasks related to the project. What would you like to do today?

Before we begin, I'll list the contents of the current directory to give you an overview of the project structure:

{""tool"":""list_directory"",""parameters"":{""path"":"".""}}

Please wait for the results...";

        // Check if this is detected as fake tool results
        var isFake = _interpreter.ContainsFakeToolResults(response, "qwen-3-coder-480b");
        _output.WriteLine($"Contains fake results: {isFake}");

        // Check if any tool calls are extracted
        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");
        if (toolCalls.Any())
        {
            foreach (var tc in toolCalls)
            {
                _output.WriteLine($"  Tool: {tc.ToolId}, Params: {string.Join(",", tc.Parameters.Select(p => $"{p.Key}={p.Value}"))}");
            }
        }

        // Check what the cleaned response looks like
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned response: {cleaned}");

        // The response should not contain the raw JSON
        Assert.DoesNotContain("{\"tool\"", cleaned);
        Assert.DoesNotContain("Please wait for the results", cleaned);
    }

    [Fact]
    public void QwenModel_MixedContentWithToolCall_ShouldExtractProperly()
    {
        // Test mixed content - text before tool call
        var response = @"I'll help you explore the directory structure.

<tool_call>
{""name"":""list_directory"",""arguments"":{""path"":"".""}}
</tool_call>";

        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        Assert.Single(toolCalls);

        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        Assert.Contains("I'll help you explore", cleaned);
        Assert.DoesNotContain("<tool_call>", cleaned);
        Assert.DoesNotContain("{\"name\"", cleaned);
    }

    [Fact]
    public void QwenModel_SystemInfoToolCall_ShouldExtractCorrectly()
    {
        // Test the exact format Qwen outputs for system_info
        var response = @"Hello! I'd be happy to help you. Let me get some system information first.

{""tool"":""system_info"",""parameters"":{}}";

        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");

        Assert.Single(toolCalls);
        Assert.Equal("system_info", toolCalls[0].ToolId);
        Assert.Empty(toolCalls[0].Parameters);

        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned response: {cleaned}");
        Assert.DoesNotContain("{\"tool\"", cleaned);
        Assert.Contains("Hello!", cleaned);
    }
}