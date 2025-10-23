using Andy.Cli.Services.Prompts;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class SystemPromptsTests
{
    [Fact]
    public void GetDefaultCliPrompt_ReturnsCompletePrompt()
    {
        // Act
        var prompt = SystemPrompts.GetDefaultCliPrompt();

        // Assert
        Assert.NotEmpty(prompt);
        Assert.Contains("Core Mandates", prompt);
        Assert.Contains("Workflow Guidelines", prompt);
        Assert.Contains("Environment Context", prompt);
        Assert.Contains("Platform:", prompt);
        Assert.Contains("Working Directory:", prompt);
        Assert.Contains("Current Date/Time:", prompt);
        Assert.Contains("Timezone:", prompt);
    }

    [Fact]
    public void GetDefaultCliPrompt_IncludesCurrentTimezone()
    {
        // Act
        var prompt = SystemPrompts.GetDefaultCliPrompt();

        // Assert
        Assert.Contains("Timezone:", prompt);
        Assert.Contains("UTC Offset:", prompt);
        Assert.Contains(TimeZoneInfo.Local.DisplayName, prompt);
    }

    [Fact]
    public void GetPromptWithTools_IncludesTools()
    {
        // Arrange
        var tools = new List<ToolInfo>
        {
            new()
            {
                Name = "datetime_tool",
                Description = "Gets current date and time",
                Parameters = new List<ToolParameterInfo>()
            }
        };

        // Act
        var prompt = SystemPrompts.GetPromptWithTools(tools);

        // Assert
        Assert.Contains("Available Tools", prompt);
        Assert.Contains("datetime_tool", prompt);
        Assert.Contains("Gets current date and time", prompt);
    }

    [Fact]
    public void GetPromptWithTools_IncludesCustomInstructions()
    {
        // Arrange
        var tools = new List<ToolInfo>();
        var customInstructions = "Model: test-model\nProvider: test-provider";

        // Act
        var prompt = SystemPrompts.GetPromptWithTools(tools, customInstructions);

        // Assert
        Assert.Contains("Custom Instructions", prompt);
        Assert.Contains("Model: test-model", prompt);
        Assert.Contains("Provider: test-provider", prompt);
    }

    [Fact]
    public void GetPromptWithTools_IncludesEnvironmentAndTimezone()
    {
        // Arrange
        var tools = new List<ToolInfo>();

        // Act
        var prompt = SystemPrompts.GetPromptWithTools(tools);

        // Assert
        Assert.Contains("Environment Context", prompt);
        Assert.Contains("Platform:", prompt);
        Assert.Contains("Timezone:", prompt);
        Assert.Contains("UTC Offset:", prompt);
    }

    [Fact]
    public void OutputFullPrompt()
    {
        // Arrange
        var tools = new List<ToolInfo>
        {
            new()
            {
                Name = "test_tool",
                Description = "A test tool"
            }
        };
        var customInstructions = "Current configuration:\n- Model: test-model\n- Provider: test-provider";

        // Act
        var prompt = SystemPrompts.GetPromptWithTools(tools, customInstructions);

        // Output to console
        Console.WriteLine("\n=== FULL SYSTEM PROMPT ===");
        Console.WriteLine(prompt);
        Console.WriteLine("=== END ===\n");

        // Dummy assert
        Assert.NotEmpty(prompt);
    }
}
