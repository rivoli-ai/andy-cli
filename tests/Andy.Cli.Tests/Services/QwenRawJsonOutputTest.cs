using System;
using System.Collections.Generic;
using Andy.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Tests for handling raw JSON output from Qwen model
/// </summary>
public class QwenRawJsonOutputTest
{
    // Parser removed; tests skipped

    [Fact(Skip = "Parser removed in structured flow")]
    public void Parse_HandlesRawJsonWithToolCall()
    {
        // Skipped
    }

    [Fact]
    public void CleanResponseText_RemovesStrayBrackets()
    {
        var input = @"}
Some text here
{
More text";

        var result = _parser.CleanResponseText(input);

        // Should remove the stray brackets
        Assert.DoesNotContain("}", result);
        Assert.DoesNotContain("{", result);
        Assert.Contains("Some text here", result);
        Assert.Contains("More text", result);
    }

    [Fact]
    public void Parse_HandlesJsonAfterText()
    {
        var input = @"Hello, what can I help you with today?
{
  ""tool_call"": {
    ""name"": ""list_directory"",
    ""arguments"": {
      ""directory_path"": "".""
    }
  }
}";

        var result = _parser.Parse(input);

        Assert.Single(result.ToolCalls);
        Assert.Equal("list_directory", result.ToolCalls[0].ToolId);
        Assert.Contains("Hello, what can I help you with today?", result.TextContent);
    }

    [Fact]
    public void Parse_HandlesResultJsonInOutput()
    {
        // This simulates when the model returns the tool result JSON
        var input = @"""contents"": [
""."",
""assets"",
""docs"",
""src"",
""tests"",
"".gitignore"",
""agents.md"",
""Andy.Cli.sln"",
""Andy.Cli.sln.DotSettings.user"",
""claude.md"",
""global.json"",
""LICENSE"",
""newfile.txt"",
""README.md""
],
""recursive"": false,
""include_hidden"": false,
""sort_by"": null,
""sort_descending"": true,
""max_depth"": 1,
""include_details"": false";

        var result = _parser.Parse(input);

        // This should not be interpreted as a tool call
        Assert.Empty(result.ToolCalls);

        // The text should be cleaned up
        var cleanedText = _parser.CleanResponseText(input);
        // Raw JSON output should be removed
        Assert.DoesNotContain("\"contents\"", cleanedText);
        Assert.DoesNotContain("\"recursive\"", cleanedText);
    }

    [Fact]
    public void CleanResponseText_RemovesRawToolResults()
    {
        var input = @"""contents"": [
""."",
""assets"",
""docs""
],
""recursive"": false";

        var result = _parser.CleanResponseText(input);

        // Should remove raw JSON tool results
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ExtractsMultipleToolCalls()
    {
        var input = @"I'll help you with that.
{
  ""tool_call"": {
    ""name"": ""list_directory"",
    ""arguments"": {
      ""directory_path"": "".""
    }
  }
}

Now let me read the file:
{
  ""tool_call"": {
    ""name"": ""read_file"",
    ""arguments"": {
      ""file_path"": ""README.md""
    }
  }
}";

        var result = _parser.Parse(input);

        // Should extract both tool calls
        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("list_directory", result.ToolCalls[0].ToolId);
        Assert.Equal("read_file", result.ToolCalls[1].ToolId);
    }

    [Fact]
    public void CleanResponseText_KeepsValidContent()
    {
        var input = @"Here are the project files:

The solution contains multiple projects organized in src/ and tests/ directories.

You can explore the code structure to understand the architecture.";

        var result = _parser.CleanResponseText(input);

        // Should keep all valid content
        Assert.Contains("Here are the project files", result);
        Assert.Contains("solution contains multiple projects", result);
        Assert.Contains("explore the code structure", result);
    }

    [Fact]
    public void Parse_HandlesQwenSpecificFormat()
    {
        // Test the exact format Qwen uses
        var input = @"}
{""tool_call"": {""name"": ""list_directory"", ""arguments"": {""directory_path"": "".""}}}";

        var result = _parser.Parse(input);

        Assert.Single(result.ToolCalls);
        Assert.Equal("list_directory", result.ToolCalls[0].ToolId);

        // Text should be empty after cleaning
        var cleanedText = _parser.CleanResponseText(result.TextContent);
        Assert.Empty(cleanedText);
    }
}