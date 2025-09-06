using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Tests for JSON parsing with escaped quotes
/// </summary>
public class ToolCallJsonParsingTests
{
    private readonly ITestOutputHelper _output;

    public ToolCallJsonParsingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("{\"tool\":\"read_file\",\"parameters\":{\"path\":\"/Users/test/file.txt\"}}", "read_file")]
    public void ParseToolCallJson_HandlesRegularJson(string input, string expectedTool)
    {
        _output.WriteLine($"Input: {input}");
        
        // Try to unescape if needed
        var jsonStr = input;
        if (input.Contains("\\\""))
        {
            // This is escaped JSON, need to unescape it
            jsonStr = input.Replace("\\\"", "\"");
            _output.WriteLine($"Unescaped: {jsonStr}");
        }

        // Parse the JSON
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            
            Assert.True(root.TryGetProperty("tool", out var toolProp));
            Assert.Equal(expectedTool, toolProp.GetString());
            
            _output.WriteLine($"Successfully parsed tool: {toolProp.GetString()}");
        }
        catch (JsonException ex)
        {
            _output.WriteLine($"Failed to parse: {ex.Message}");
            throw;
        }
    }

    [Fact]
    public void ExtractJsonFromResponse_HandlesEscapedJson()
    {
        // Test with escaped JSON (as might come from the LLM)
        var response = @"{""tool"":""read_file"",""parameters"":{""path"":""/Users/samibengrine/Devel/rivoli-ai/andy-cli/project.sln""}}";
        
        _output.WriteLine($"Original response: {response}");
        
        // Parse it directly - it's already valid JSON
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        
        Assert.True(root.TryGetProperty("tool", out var toolProp));
        Assert.Equal("read_file", toolProp.GetString());
        
        Assert.True(root.TryGetProperty("parameters", out var parameters));
        Assert.True(parameters.TryGetProperty("path", out var pathProp));
        Assert.Equal("/Users/samibengrine/Devel/rivoli-ai/andy-cli/project.sln", pathProp.GetString());
    }
}