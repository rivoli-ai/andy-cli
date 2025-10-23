using Andy.Cli.Services.Prompts;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class SystemPromptBuilderTests
{
    [Fact]
    public void SystemPrompt_IncludesCodeDisplayGuidance()
    {
        // Arrange
        var builder = new SystemPromptBuilder();

        // Act
        var prompt = builder
            .WithCoreMandates()
            .WithWorkflowGuidelines()
            .Build();

        // Assert
        Assert.Contains("Display the code/text directly in your response", prompt);
        Assert.Contains("Do NOT use the write_file tool unless explicitly asked to save/create a file", prompt);
        Assert.Contains("write a fibonacci program", prompt);
        Assert.Contains("display inline", prompt);
    }

    [Fact]
    public void SystemPrompt_DistinguishesBetweenDisplayAndSave()
    {
        // Arrange
        var builder = new SystemPromptBuilder();

        // Act
        var prompt = builder
            .WithCoreMandates()
            .WithWorkflowGuidelines()
            .Build();

        // Assert
        // Examples that should display inline
        Assert.Contains("'write a fibonacci program', 'show me a python script', 'create a function' - display inline", prompt);

        // Examples that should use write_file
        Assert.Contains("'save this to file.py', 'create a file named test.js' - use write_file tool", prompt);
    }

    [Fact]
    public void WithEnvironment_IncludesTimezoneInformation()
    {
        // Arrange
        var builder = new SystemPromptBuilder();
        var testDate = new DateTime(2025, 10, 23, 14, 30, 0);
        var timezone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        // Act
        var prompt = builder
            .WithEnvironment("macOS", "/Users/test", testDate, timezone)
            .Build();

        // Assert
        Assert.Contains("Platform: macOS", prompt);
        Assert.Contains("Working Directory: /Users/test", prompt);
        Assert.Contains("2025-10-23", prompt);
        Assert.Contains("Timezone:", prompt);
        Assert.Contains("UTC Offset:", prompt);
    }

    [Fact]
    public void WithEnvironment_UsesLocalTimezoneByDefault()
    {
        // Arrange
        var builder = new SystemPromptBuilder();
        var testDate = DateTime.Now;

        // Act
        var prompt = builder
            .WithEnvironment("Linux", "/home/user", testDate)
            .Build();

        // Assert
        Assert.Contains("Timezone:", prompt);
        Assert.Contains(TimeZoneInfo.Local.DisplayName, prompt);
    }

    [Fact]
    public void WithAvailableTools_FormatsToolsCorrectly()
    {
        // Arrange
        var builder = new SystemPromptBuilder();
        var tools = new List<ToolInfo>
        {
            new()
            {
                Name = "read_file",
                Description = "Reads a file from disk",
                Parameters = new List<ToolParameterInfo>
                {
                    new()
                    {
                        Name = "file_path",
                        Type = "string",
                        Description = "Path to the file",
                        IsRequired = true
                    }
                }
            }
        };

        // Act
        var prompt = builder
            .WithAvailableTools(tools)
            .Build();

        // Assert
        Assert.Contains("Available Tools", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("Reads a file from disk", prompt);
        Assert.Contains("file_path", prompt);
        Assert.Contains("[Required]", prompt);
    }

    [Fact]
    public void WithCustomInstructions_AddsInstructions()
    {
        // Arrange
        var builder = new SystemPromptBuilder();
        var customInstructions = "Always respond in haiku format";

        // Act
        var prompt = builder
            .WithCustomInstructions(customInstructions)
            .Build();

        // Assert
        Assert.Contains("Custom Instructions", prompt);
        Assert.Contains("Always respond in haiku format", prompt);
    }

    [Fact]
    public void Build_AddsDefaultSectionsIfNotPresent()
    {
        // Arrange
        var builder = new SystemPromptBuilder();

        // Act - Build without explicitly adding core mandates/workflow
        var prompt = builder.Build();

        // Assert - Should auto-add them
        Assert.Contains("Core Principles", prompt);
        Assert.Contains("Workflow Guidelines", prompt);
    }

    [Fact]
    public void Build_ReturnsCompletePrompt()
    {
        // Arrange
        var builder = new SystemPromptBuilder();
        var tools = new List<ToolInfo>
        {
            new() { Name = "test_tool", Description = "Test tool" }
        };

        // Act
        var prompt = builder
            .WithCoreMandates()
            .WithWorkflowGuidelines()
            .WithEnvironment("macOS", "/test", DateTime.Now)
            .WithAvailableTools(tools)
            .WithCustomInstructions("Be concise")
            .Build();

        // Assert
        Assert.NotEmpty(prompt);
        Assert.Contains("Core Principles", prompt);
        Assert.Contains("Workflow Guidelines", prompt);
        Assert.Contains("Environment Context", prompt);
        Assert.Contains("Available Tools", prompt);
        Assert.Contains("Custom Instructions", prompt);
    }
}
