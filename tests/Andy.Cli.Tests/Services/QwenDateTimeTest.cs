using System.Linq;
using Andy.Cli.Services;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

public class QwenDateTimeTest
{
    private readonly ModelResponseInterpreter _interpreter;
    private readonly ITestOutputHelper _output;

    public QwenDateTimeTest(ITestOutputHelper output)
    {
        _interpreter = new ModelResponseInterpreter();
        _output = output;
    }

    [Fact]
    public void QwenModel_DateTimeToolWithNestedParams_ShouldExtract()
    {
        // This is the EXACT output the user is seeing
        var response = @"{""tool"":""datetime_tool"",""parameters"":{""operation"":""get_current_time""}}";

        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");
        
        if (toolCalls.Any())
        {
            foreach (var tc in toolCalls)
            {
                _output.WriteLine($"  Tool: {tc.ToolId}");
                foreach (var p in tc.Parameters)
                {
                    _output.WriteLine($"    {p.Key} = {p.Value}");
                }
            }
        }
        
        Assert.Single(toolCalls);
        Assert.Equal("datetime_tool", toolCalls[0].ToolId);
        Assert.Single(toolCalls[0].Parameters);
        Assert.Equal("get_current_time", toolCalls[0].Parameters["operation"]);
    }
    
    [Fact]
    public void QwenModel_HelloWithDateTime_ShouldExtractAndClean()
    {
        // More realistic example with greeting text
        var response = @"Hello! I'm here to help you. Let me check the current time for you.

{""tool"":""datetime_tool"",""parameters"":{""operation"":""get_current_time""}}";

        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");
        
        Assert.Single(toolCalls);
        Assert.Equal("datetime_tool", toolCalls[0].ToolId);
        Assert.Equal("get_current_time", toolCalls[0].Parameters["operation"]);
        
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned: '{cleaned}'");
        
        Assert.DoesNotContain("{\"tool\"", cleaned);
        Assert.Contains("Hello", cleaned);
    }
}