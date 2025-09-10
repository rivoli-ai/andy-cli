using Xunit;
using System.Text.RegularExpressions;

namespace Andy.Cli.Tests.Services;

public class ResponseFormattingTests
{
    /// <summary>
    /// Simulates the SanitizeAssistantText method logic for testing
    /// </summary>
    private string SanitizeAssistantText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sanitized = text;

        // Remove outer quotes if the entire content is quoted
        if (sanitized.StartsWith("\"") && sanitized.EndsWith("\"") && sanitized.Length > 1)
        {
            sanitized = sanitized.Substring(1, sanitized.Length - 2);
        }

        // Unescape common escape sequences
        sanitized = sanitized.Replace("\\n", "\n")
                           .Replace("\\r", "\r")
                           .Replace("\\t", "\t")
                           .Replace("\\\"", "\"")
                           .Replace("\\'", "'")
                           .Replace("\\\\", "\\")
                           .Replace("\\u0022", "\"");

        // Hide internal tool mentions - remove lines that are just tool references
        var lines = sanitized.Split('\n');
        var filteredLines = new List<string>();
        
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            // Skip lines that look like internal tool mentions
            if (ShouldHideToolMention(trimmedLine))
                continue;
            filteredLines.Add(line);
        }

        sanitized = string.Join('\n', filteredLines);

        // Trim excessive whitespace and remove multiple consecutive blank lines
        sanitized = sanitized.Trim();
        
        // Replace multiple consecutive newlines with at most two
        while (sanitized.Contains("\n\n\n"))
        {
            sanitized = sanitized.Replace("\n\n\n", "\n\n");
        }

        return sanitized;
    }

    private bool ShouldHideToolMention(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        // Common patterns for internal tool mentions to hide
        var patterns = new[]
        {
            @"^\[?Tool\s+(Execution|call|Result)",
            @"^Executing\s+tool:",
            @"^Calling\s+\w+\s+tool",
            @"^Using\s+\w+\s+tool",
            @"^<tool_call>",
            @"^</tool_call>",
            @"^\{""tool"":",
            @"^\{""name"":\s*""\w+""",
            @"^Tool\s+\w+\s+called",
            @"^I'?m?\s+(going\s+to\s+)?use\s+the\s+\w+\s+tool",
            @"^Let\s+me\s+use\s+the\s+\w+\s+tool"
        };

        return patterns.Any(pattern => Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase));
    }
    
    [Fact]
    public void SanitizeAssistantText_RemovesExcessiveBlankLines()
    {
        // Arrange
        var input = "Line 1\n\n\n\n\nLine 2\n\n\n\nLine 3";
        
        // Act
        var result = SanitizeAssistantText(input);
        
        // Assert
        Assert.DoesNotContain("\n\n\n", result);
        Assert.Contains("Line 1", result);
        Assert.Contains("Line 2", result);
        Assert.Contains("Line 3", result);
    }
    
    [Fact]
    public void SanitizeAssistantText_PreservesNormalLineBreaks()
    {
        // Arrange
        var input = "Line 1\nLine 2\n\nLine 3";
        
        // Act
        var result = SanitizeAssistantText(input);
        
        // Assert
        Assert.Equal("Line 1\nLine 2\n\nLine 3", result);
    }
    
    [Fact]
    public void SanitizeAssistantText_RemovesOuterQuotes()
    {
        // Arrange
        var input = "\"This is quoted text\"";
        
        // Act
        var result = SanitizeAssistantText(input);
        
        // Assert
        Assert.Equal("This is quoted text", result);
    }
    
    [Fact]
    public void SanitizeAssistantText_UnescapesSequences()
    {
        // Arrange
        var input = "Line 1\\nLine 2\\t\\\"quoted\\\"";
        
        // Act
        var result = SanitizeAssistantText(input);
        
        // Assert
        Assert.Contains("Line 1\nLine 2", result);
        Assert.Contains("\t", result);
        Assert.Contains("\"quoted\"", result);
    }
    
    [Fact]
    public void SanitizeAssistantText_TrimsWhitespace()
    {
        // Arrange
        var input = "  \n\n  Some text  \n\n  ";
        
        // Act
        var result = SanitizeAssistantText(input);
        
        // Assert
        Assert.Equal("Some text", result);
    }
    
    [Fact]
    public void SanitizeAssistantText_HandlesComplexFormattingIssues()
    {
        // Arrange - simulating the problematic output from .NET 10 upgrade query
        var input = @"Here's how to upgrade:

```json
{""tool"":""code_index"",""parameters"":{""query_type"":""structure""}}
```


First, update the global.json file:



```json
{
  ""sdk"": {
    ""version"": ""10.0.100""
  }
}
```



Then update project files:


```xml
<TargetFramework>net10.0</TargetFramework>
```


Done!";
        
        // Act
        var result = SanitizeAssistantText(input);
        
        // Assert
        // Should not have more than 2 consecutive newlines
        Assert.DoesNotContain("\n\n\n", result);
        // Should preserve code blocks
        Assert.Contains("```json", result);
        Assert.Contains("```xml", result);
    }
    
    [Fact]
    public void SanitizeAssistantText_RemovesToolMentions()
    {
        // Arrange
        var input = @"Let me search for that.
[Tool Execution: search_files]
I'm going to use the list_directory tool
<tool_call>
{""tool"":""list_directory""}
</tool_call>

Here are the results:
- File1.cs
- File2.cs";
        
        // Act
        var result = SanitizeAssistantText(input);
        
        // Assert
        Assert.DoesNotContain("[Tool Execution", result);
        Assert.DoesNotContain("use the list_directory tool", result);
        Assert.DoesNotContain("<tool_call>", result);
        Assert.DoesNotContain("{\"tool\":", result);
        Assert.Contains("Let me search for that", result);
        Assert.Contains("Here are the results", result);
        Assert.Contains("File1.cs", result);
    }
}