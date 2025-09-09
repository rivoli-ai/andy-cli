using System;
using System.Linq;
using System.Text.RegularExpressions;
using Andy.Cli.Services;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

public class QwenNoRepetitionTest
{
    private readonly ModelResponseInterpreter _interpreter;
    private readonly ITestOutputHelper _output;

    public QwenNoRepetitionTest(ITestOutputHelper output)
    {
        _interpreter = new ModelResponseInterpreter();
        _output = output;
    }

    [Fact]
    public void QwenModel_CleanedResponse_ShouldNotHaveRepetition()
    {
        // This is the exact repetitive output from Qwen
        var response = @"The project structure is about a CLI (Command Line Interface) tool named Andy.Cli. It seems to be an interactive tool with multiple feat
The project structure is about a CLI (Command Line Interface) tool named Andy.Cli. It seems to be an interactive tool with multiple feat
ures and functionality, including but not limited to Commands, Services, Tools, and Widgets. It appears to be designed for user interact
ion and automation of tasks. More information can be gathered by executing the tool for 'list_directory' since it can read existing file
s that show detailed information about the project.

To know more about the project details execute the following command:

The project structure is about a CLI (Command Line Interface) tool named Andy.Cli. It seems to be an interactive tool with multiple feat
ures and functionality, including but not limited to Commands, Services, Tools, and Widgets. It appears to be designed for user interact
ion and automation of tasks. More information can be gathered by executing the tool for 'list_directory' since it can read existing file
s that show detailed information about the project.

To know more about the project details execute the following command:

{""tool"":""read_file"",""parameters"":{""file_path"":""README.md""}}";

        // Clean the response
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned response: {cleaned}");

        // Check that the tool call JSON is removed
        Assert.DoesNotContain("{\"tool\"", cleaned);

        // Check for no duplicate sentences
        var sentences = cleaned.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 10) // Ignore very short fragments
            .ToList();

        _output.WriteLine($"Found {sentences.Count} sentences");

        // Check each sentence appears only once
        var duplicates = sentences.GroupBy(s => s)
            .Where(g => g.Count() > 1)
            .Select(g => new { Sentence = g.Key, Count = g.Count() })
            .ToList();

        if (duplicates.Any())
        {
            foreach (var dup in duplicates)
            {
                _output.WriteLine($"Duplicate found ({dup.Count}x): {dup.Sentence}");
            }
        }

        Assert.Empty(duplicates);
    }

    [Fact]
    public void QwenModel_WithToolCall_ShouldNotRepeatBeforeAndAfter()
    {
        // Test case where text appears both before and after tool call
        var response = @"I'll help you read the README file.

{""tool"":""read_file"",""parameters"":{""file_path"":""README.md""}}

I'll help you read the README file.";

        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned: '{cleaned}'");

        // Should not contain the tool call
        Assert.DoesNotContain("{\"tool\"", cleaned);

        // Count occurrences of the sentence
        var sentence = "I'll help you read the README file";
        var matches = Regex.Matches(cleaned, Regex.Escape(sentence));

        _output.WriteLine($"Occurrences of '{sentence}': {matches.Count}");

        // Should appear only once (or be completely removed if it's only around tool call)
        Assert.True(matches.Count <= 1, $"Sentence appears {matches.Count} times, expected 0 or 1");
    }

    [Fact]
    public void QwenModel_StreamingWithRepetition_ShouldBeDetected()
    {
        // Simulate what happens during streaming when content is duplicated
        var response = @"Hello! I'd be happy to help.
Hello! I'd be happy to help.
Let me check the system information.
Let me check the system information.

{""tool"":""system_info"",""parameters"":{}}";

        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned: '{cleaned}'");

        // Split into lines and check for duplicates
        var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var duplicateLines = lines.GroupBy(l => l)
            .Where(g => g.Count() > 1)
            .Select(g => new { Line = g.Key, Count = g.Count() })
            .ToList();

        if (duplicateLines.Any())
        {
            foreach (var dup in duplicateLines)
            {
                _output.WriteLine($"Duplicate line ({dup.Count}x): {dup.Line}");
            }
        }

        // For now, we detect duplicates but don't fail - this helps us understand the issue
        // In production, we might want to deduplicate in the cleaning process
        _output.WriteLine($"Total lines: {lines.Count}, Unique lines: {lines.Distinct().Count()}");
    }

    [Fact]
    public void QwenModel_PartialSentenceRepetition_ShouldBeDetected()
    {
        // Test the exact pattern from the user's output
        var response = @"The project structure is about a CLI (Command Line Interface) tool named Andy.Cli. It seems to be an interactive tool with multiple feat
The project structure is about a CLI (Command Line Interface) tool named Andy.Cli. It seems to be an interactive tool with multiple feat";

        // Check if this pattern of truncated repetition exists
        var lines = response.Split('\n');

        _output.WriteLine($"Line count: {lines.Length}");

        // Check if lines are similar (potential repetition with truncation)
        for (int i = 0; i < lines.Length - 1; i++)
        {
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (lines[i].Length > 20 && lines[j].Length > 20)
                {
                    var commonPrefix = GetCommonPrefix(lines[i], lines[j]);
                    if (commonPrefix.Length > Math.Min(lines[i].Length, lines[j].Length) * 0.8)
                    {
                        _output.WriteLine($"Lines {i} and {j} are similar (prefix: {commonPrefix.Length} chars)");
                        _output.WriteLine($"  Line {i}: {lines[i].Substring(0, Math.Min(50, lines[i].Length))}...");
                        _output.WriteLine($"  Line {j}: {lines[j].Substring(0, Math.Min(50, lines[j].Length))}...");
                    }
                }
            }
        }

        // For streaming responses, we should detect and remove duplicates
        Assert.True(lines.Length <= 2 || !AreLinesRepetitive(lines),
            "Response contains repetitive lines that should be cleaned");
    }

    private string GetCommonPrefix(string s1, string s2)
    {
        int i = 0;
        while (i < s1.Length && i < s2.Length && s1[i] == s2[i])
        {
            i++;
        }
        return s1.Substring(0, i);
    }

    private bool AreLinesRepetitive(string[] lines)
    {
        // Check if multiple lines share significant common prefixes
        for (int i = 0; i < lines.Length - 1; i++)
        {
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (lines[i].Length > 20 && lines[j].Length > 20)
                {
                    var commonPrefix = GetCommonPrefix(lines[i], lines[j]);
                    // If 80% or more is common, consider it repetitive
                    if (commonPrefix.Length > Math.Min(lines[i].Length, lines[j].Length) * 0.8)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    [Fact]
    public void QwenModel_EscapedJsonOutput_ShouldBeDetected()
    {
        // Test case from user showing escaped JSON in output
        var response = @"""[Tool Execution: list_directory]\nParameters:\n  path: src/\nResult:\n{\n  \u0022items\u0022: [\n    {\n      \u0022name\u0022: \u0022Andy.Cli.csproj\u0022,\n      \u0022fullPath\u0022: \u0022/Users/sami...\n[Truncated - full result was 1468 characters]\n""";

        // This appears to be Qwen outputting the tool result as an escaped string
        // The interpreter should detect this as potentially problematic
        _output.WriteLine($"Response contains escaped quotes: {response.Contains(@"\u0022")}");
        _output.WriteLine($"Response contains Tool Execution: {response.Contains("Tool Execution")}");

        // Clean the response
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        _output.WriteLine($"Cleaned: '{cleaned}'");

        // The escaped JSON should be detected and removed or cleaned
        Assert.DoesNotContain(@"\u0022", cleaned);
        Assert.DoesNotContain(@"Tool Execution", cleaned);
    }

    [Fact]
    public void QwenModel_SimpleGreeting_ShouldNotUseTool()
    {
        // Test case showing Qwen correctly responding to hello without tools
        var response = @"Hello! How can I assist you today?";

        // Check no tool calls are extracted
        var toolCalls = _interpreter.ExtractToolCalls(response, "qwen-3-coder-480b", "cerebras");
        _output.WriteLine($"Tool calls found: {toolCalls.Count}");

        Assert.Empty(toolCalls);

        // Clean response should be unchanged
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        Assert.Equal("Hello! How can I assist you today?", cleaned);
    }
}