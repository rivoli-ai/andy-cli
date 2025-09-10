using Xunit;
using Andy.Cli.Services;

namespace Andy.Cli.Tests.Services;

public class ToolOutputLimitsTests
{
    [Theory]
    [InlineData("read_file", 1500)]
    [InlineData("search_files", 1000)]
    [InlineData("bash_command", 1000)]
    [InlineData("bash", 1000)]
    [InlineData("list_directory", 800)]
    [InlineData("unknown_tool", 1000)] // Should use default
    public void GetLimit_ReturnsCorrectLimits(string toolId, int expectedLimit)
    {
        // Act
        var limit = ToolOutputLimits.GetLimit(toolId);
        
        // Assert
        Assert.Equal(expectedLimit, limit);
    }
    
    [Fact]
    public void LimitOutput_DoesNotTruncateSmallOutput()
    {
        // Arrange
        var output = "Small output";
        
        // Act
        var limited = ToolOutputLimits.LimitOutput("read_file", output);
        
        // Assert
        Assert.Equal(output, limited);
    }
    
    [Fact]
    public void LimitOutput_TruncatesLargeOutput()
    {
        // Arrange
        var largeOutput = new string('x', 10000);
        
        // Act
        var limited = ToolOutputLimits.LimitOutput("read_file", largeOutput);
        
        // Assert
        Assert.True(limited.Length < largeOutput.Length);
        Assert.Contains("[Output truncated", limited);
        Assert.Contains("Tool: read_file", limited);
        // Should be around 1500 chars (the limit for read_file)
        Assert.True(limited.Length < 1700); // Some extra for truncation message
    }
    
    [Fact]
    public void LimitOutput_TruncatesAtNaturalBoundary()
    {
        // Arrange
        var output = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"Line {i}: " + new string('x', 50)));
        
        // Act
        var limited = ToolOutputLimits.LimitOutput("search_text", output);
        
        // Assert
        // Should truncate at a newline boundary
        Assert.Contains("[Output truncated", limited);
        Assert.Contains("Tool: search_text", limited);
        // Verify it's actually truncated
        Assert.True(limited.Length < output.Length);
    }
    
    [Fact]
    public void LimitOutput_HandlesNullAndEmpty()
    {
        // Act & Assert
        Assert.Equal(null, ToolOutputLimits.LimitOutput("test", null!));
        Assert.Equal("", ToolOutputLimits.LimitOutput("test", ""));
    }
    
    [Fact]
    public void LimitOutput_UsesDifferentLimitsPerTool()
    {
        // Arrange
        var largeOutput = new string('x', 10000);
        
        // Act
        var readFileOutput = ToolOutputLimits.LimitOutput("read_file", largeOutput);
        var bashOutput = ToolOutputLimits.LimitOutput("bash_command", largeOutput);
        
        // Assert
        // read_file has 1500 limit, bash_command has 1000 limit
        Assert.True(readFileOutput.Length > bashOutput.Length);
        Assert.Contains("Tool: read_file", readFileOutput);
        Assert.Contains("Tool: bash_command", bashOutput);
    }
}