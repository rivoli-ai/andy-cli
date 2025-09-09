using System;
using System.Collections.Generic;
using Andy.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Tests for Qwen response cleanup patterns
/// </summary>
public class QwenCleanupPatternsTest
{
    private readonly QwenResponseParser _parser;

    public QwenCleanupPatternsTest()
    {
        _parser = new QwenResponseParser(
            new JsonRepairService(),
            new StreamingToolCallAccumulator(new JsonRepairService(), null),
            NullLogger<QwenResponseParser>.Instance);
    }

    [Fact]
    public void CleanResponseText_RemovesYouWithInvisibleCharacters()
    {
        // Test various forms of "You" with invisible characters
        var testCases = new[]
        {
            "Y‌ou should see this",  // With zero-width non-joiner
            "Y\u200Cou are here",     // With explicit zero-width non-joiner
            "Y\u200Dou can do this",  // With zero-width joiner
            "Y\uFEFFou will see",     // With zero-width no-break space
            "You normal text"          // Normal "You" should remain
        };

        foreach (var input in testCases)
        {
            var result = _parser.CleanResponseText(input);

            if (input == "You normal text")
            {
                Assert.Contains("You normal text", result);
            }
            else
            {
                Assert.DoesNotContain("Y", result);
                Assert.NotEmpty(result); // Should keep the rest of the text
            }
        }
    }

    [Fact]
    public void CleanResponseText_RemovesTryThis()
    {
        var input = "Try this: let me search for files.";
        var result = _parser.CleanResponseText(input);

        Assert.DoesNotContain("Try this", result);
        Assert.Empty(result); // The entire thought should be removed
    }

    [Fact]
    public void CleanResponseText_RemovesToolResultsWithJson()
    {
        var input = @"Here is the analysis.

[Tool Results]
{""files"": [""Andy.Cli.csproj"", ""Program.cs""], ""directories"": [""Services"", ""Commands""]}

The solution contains these files.";

        var result = _parser.CleanResponseText(input);

        Assert.DoesNotContain("[Tool Results]", result);
        Assert.DoesNotContain("\"files\"", result);
        Assert.Contains("Here is the analysis", result);
        Assert.Contains("The solution contains these files", result);
    }

    [Fact]
    public void CleanResponseText_RemovesMultilineToolResults()
    {
        var input = @"Analysis complete.

[Tool Results]
{""files"": [""Andy.Cli.csproj"", ""AndyLlmIntegrationTests.cs"", ""CliWidgetsTests.cs"", ""Program.cs""], 
 ""directories"": [""Integration"", ""Services"", ""TestData"", ""Widgets"", ""Chat"", ""Commands""], 
 ""path"": ""src"", 
 ""subdirectories"": {
    ""Integration"": ""src/Integration"", 
    ""Services"": ""src/Services""
 }}

Summary provided above.";

        var result = _parser.CleanResponseText(input);

        Assert.DoesNotContain("[Tool Results]", result);
        Assert.DoesNotContain("files", result);
        Assert.DoesNotContain("subdirectories", result);
        Assert.Contains("Analysis complete", result);
        Assert.Contains("Summary provided above", result);
    }

    [Fact]
    public void CleanResponseText_RemovesInternalThoughts()
    {
        var testCases = new[]
        {
            "Let me check the files for you.",
            "I'll search the directory now.",
            "I need to examine the code.",
            "I'm going to analyze this.",
            "Now I'll run the tool.",
            "Try this: search for patterns.",
            "Looking for configuration files.",
            "Checking the solution structure."
        };

        foreach (var input in testCases)
        {
            var result = _parser.CleanResponseText(input);
            Assert.Empty(result);
        }
    }

    [Fact]
    public void CleanResponseText_PreservesNormalContent()
    {
        var input = @"The solution contains the following structure:

- Source code in the src/ directory
- Tests in the tests/ directory
- Documentation in docs/

This is a typical .NET solution layout.";

        var result = _parser.CleanResponseText(input);

        Assert.Contains("solution contains", result);
        Assert.Contains("Source code", result);
        Assert.Contains("Tests in", result);
        Assert.Contains("Documentation", result);
        Assert.Contains("typical .NET solution", result);
    }

    [Fact]
    public void CleanResponseText_HandlesCombinedPatterns()
    {
        var input = @"Y‌ou asked about the solution.
Try this: let me analyze it.

[Tool Results]
{""tool"": ""list_directory"", ""result"": ""success""}

I'll check the files now.
The solution has multiple projects.";

        var result = _parser.CleanResponseText(input);

        Assert.DoesNotContain("Y‌ou", result);
        Assert.DoesNotContain("Try this", result);
        Assert.DoesNotContain("[Tool Results]", result);
        Assert.DoesNotContain("I'll check", result);
        Assert.Contains("The solution has multiple projects", result);
    }

    [Fact]
    public void CleanResponseText_HandlesEmptyAndNull()
    {
        Assert.Equal("", _parser.CleanResponseText(""));
        Assert.Equal("", _parser.CleanResponseText("   "));
        Assert.Equal("", _parser.CleanResponseText(null!));
    }

    [Fact]
    public void CleanResponseText_RemovesExcessWhitespace()
    {
        var input = @"Text   with    multiple     spaces.


Multiple

Newlines

Here.";

        var result = _parser.CleanResponseText(input);

        Assert.DoesNotContain("  ", result); // No double spaces
        Assert.DoesNotContain("\n\n", result); // No double newlines
        Assert.Contains("Text with multiple spaces", result);
        Assert.Contains("Multiple\nNewlines\nHere", result);
    }

    [Theory]
    [InlineData("Let me help you with that.", "")]
    [InlineData("I can assist with this task.", "")]
    [InlineData("Searching for the information now.", "")]
    [InlineData("Going to analyze the code.", "")]
    [InlineData("Your code looks good.", "Your code looks good.")]
    [InlineData("The function works correctly.", "The function works correctly.")]
    public void CleanResponseText_FiltersCorrectly(string input, string expected)
    {
        var result = _parser.CleanResponseText(input);
        Assert.Equal(expected, result);
    }
}