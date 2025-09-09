using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Llm.Models;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Tests for tool call extraction from LLM responses
/// </summary>
public class ToolCallExtractionTests
{
    private readonly ITestOutputHelper _output;

    public ToolCallExtractionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("{\"tool\":\"copy_file\",\"parameters\":{\"source_path\":\"claude.md\",\"destination_path\":\"./tmp\"}}", "copy_file")]
    [InlineData("{\"function\":\"read_file\",\"arguments\":{\"path\":\"test.txt\"}}", "read_file")]
    [InlineData("<tool_use>{\"tool\":\"list_directory\",\"parameters\":{}}</tool_use>", "list_directory")]
    [InlineData("Let me help you with that. {\"tool\":\"write_file\",\"parameters\":{\"content\":\"test\"}}", "write_file")]
    public void ExtractToolCalls_DetectsVariousFormats(string response, string expectedToolId)
    {
        // Arrange
        var service = new ToolCallExtractor();
        var llmResponse = new LlmResponse { Content = response };

        // Act
        var toolCalls = service.ExtractToolCalls(llmResponse);
        _output.WriteLine($"Response: {response}");
        _output.WriteLine($"Extracted: {toolCalls.Count} tool calls");
        if (toolCalls.Any())
        {
            _output.WriteLine($"Tool ID: {toolCalls.First().ToolId}");
        }

        // Assert
        Assert.NotEmpty(toolCalls);
        Assert.Equal(expectedToolId, toolCalls.First().ToolId);
    }

    [Theory]
    [InlineData("{\"tool\":\"copy_file\",\"parameters\":{\"source_path\":\"claude.md\",\"destination_path\":\"./tmp\"}}", "source_path", "claude.md")]
    [InlineData("{\"tool\":\"copy_file\",\"parameters\":{\"source_path\":\"claude.md\",\"destination_path\":\"./tmp\"}}", "destination_path", "./tmp")]
    [InlineData("{\"function\":\"write_file\",\"arguments\":{\"content\":\"hello\",\"path\":\"test.txt\"}}", "content", "hello")]
    public void ExtractToolCalls_ExtractsParameters(string response, string paramName, string expectedValue)
    {
        // Arrange
        var service = new ToolCallExtractor();
        var llmResponse = new LlmResponse { Content = response };

        // Act
        var toolCalls = service.ExtractToolCalls(llmResponse);

        // Assert
        Assert.NotEmpty(toolCalls);
        var toolCall = toolCalls.First();
        Assert.True(toolCall.Parameters.ContainsKey(paramName));
        Assert.Equal(expectedValue, toolCall.Parameters[paramName]?.ToString());
    }

    [Fact]
    public void ExtractToolCalls_HandlesMultipleToolCalls()
    {
        // Arrange
        var service = new ToolCallExtractor();
        var response = @"
<tool_use>{""tool"":""create_directory"",""parameters"":{""path"":""./tmp""}}</tool_use>
<tool_use>{""tool"":""copy_file"",""parameters"":{""source_path"":""claude.md"",""destination_path"":""./tmp/claude.md""}}</tool_use>";
        var llmResponse = new LlmResponse { Content = response };

        // Act
        var toolCalls = service.ExtractToolCalls(llmResponse);

        // Assert
        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("create_directory", toolCalls[0].ToolId);
        Assert.Equal("copy_file", toolCalls[1].ToolId);
    }

    [Theory]
    [InlineData("Just a regular response with no tools", 0)]
    [InlineData("Here's how you can do it: first run this command", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ExtractToolCalls_ReturnsEmptyForNonToolResponses(string? response, int expectedCount)
    {
        // Arrange
        var service = new ToolCallExtractor();
        var llmResponse = new LlmResponse { Content = response };

        // Act
        var toolCalls = service.ExtractToolCalls(llmResponse);

        // Assert
        Assert.Equal(expectedCount, toolCalls.Count);
    }
}

/// <summary>
/// Helper class to test tool call extraction logic
/// </summary>
public class ToolCallExtractor
{
    public List<ToolCall> ExtractToolCalls(LlmResponse response)
    {
        var toolCalls = new List<ToolCall>();

        if (string.IsNullOrEmpty(response.Content))
        {
            return toolCalls;
        }

        var content = response.Content.Trim();

        // First try to extract from <tool_use> blocks
        toolCalls = ExtractWrappedToolCalls(content);

        // If no wrapped tool calls found, try to parse as direct JSON
        if (!toolCalls.Any())
        {
            toolCalls = ExtractDirectJsonToolCalls(content);
        }

        return toolCalls;
    }

    private List<ToolCall> ExtractWrappedToolCalls(string content)
    {
        var toolCalls = new List<ToolCall>();
        var startTag = "<tool_use>";
        var endTag = "</tool_use>";

        var startIndex = 0;
        while ((startIndex = content.IndexOf(startTag, startIndex, System.StringComparison.OrdinalIgnoreCase)) != -1)
        {
            var endIndex = content.IndexOf(endTag, startIndex, System.StringComparison.OrdinalIgnoreCase);
            if (endIndex == -1) break;

            var toolJson = content.Substring(
                startIndex + startTag.Length,
                endIndex - startIndex - startTag.Length).Trim();

            var toolCall = ParseToolCallJson(toolJson);
            if (toolCall != null)
            {
                toolCalls.Add(toolCall);
            }

            startIndex = endIndex + endTag.Length;
        }

        return toolCalls;
    }

    private List<ToolCall> ExtractDirectJsonToolCalls(string content)
    {
        var toolCalls = new List<ToolCall>();

        // Clean up the content first (remove code blocks)
        content = content.Replace("```json", "").Replace("```", "").Trim();

        // Check if the content contains tool/function JSON (don't require it to start with {)
        if (!content.Contains("{") || (!content.Contains("\"tool\"") && !content.Contains("\"function\"")))
        {
            return toolCalls;
        }

        // Try to find JSON objects in the content
        var jsonStart = content.IndexOf('{');
        while (jsonStart >= 0)
        {
            // Find matching closing brace
            int braceCount = 0;
            int jsonEnd = jsonStart;

            for (int i = jsonStart; i < content.Length; i++)
            {
                if (content[i] == '{')
                    braceCount++;
                else if (content[i] == '}')
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        jsonEnd = i;
                        break;
                    }
                }
            }

            if (jsonEnd > jsonStart)
            {
                var jsonStr = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var toolCall = ParseToolCallJson(jsonStr);

                if (toolCall != null)
                {
                    toolCalls.Add(toolCall);

                    // For now, only handle the first tool call in direct JSON format
                    // Most LLMs return a single tool call at a time
                    break;
                }
            }

            // Look for next potential JSON object
            jsonStart = content.IndexOf('{', jsonEnd + 1);
        }

        return toolCalls;
    }

    private ToolCall? ParseToolCallJson(string jsonStr)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            // Check if this looks like a tool call
            if (!root.TryGetProperty("tool", out _) && !root.TryGetProperty("function", out _))
            {
                return null;
            }

            var toolCall = new ToolCall();

            // Support both "tool" and "function" property names
            if (root.TryGetProperty("tool", out var toolProp))
            {
                toolCall.ToolId = toolProp.GetString() ?? "";
            }
            else if (root.TryGetProperty("function", out var funcProp))
            {
                toolCall.ToolId = funcProp.GetString() ?? "";
            }

            // Support both "parameters" and "arguments" property names  
            if (root.TryGetProperty("parameters", out var parameters))
            {
                ParseParameters(parameters, toolCall.Parameters);
            }
            else if (root.TryGetProperty("arguments", out var arguments))
            {
                ParseParameters(arguments, toolCall.Parameters);
            }

            return toolCall;
        }
        catch (System.Text.Json.JsonException)
        {
            // Not valid JSON or not a tool call
            return null;
        }
    }

    private void ParseParameters(System.Text.Json.JsonElement parameters, Dictionary<string, object?> target)
    {
        foreach (var param in parameters.EnumerateObject())
        {
            target[param.Name] = param.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => param.Value.GetString(),
                System.Text.Json.JsonValueKind.Number => param.Value.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                System.Text.Json.JsonValueKind.Array => param.Value.GetRawText(),
                System.Text.Json.JsonValueKind.Object => param.Value.GetRawText(),
                _ => param.Value.GetRawText()
            };
        }
    }
}