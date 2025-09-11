using System.Collections.Generic;
using Andy.Cli.Services;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class SystemPromptTests
{
    [Fact]
    public void SystemPrompt_Should_Include_Greeting_Instructions()
    {
        // Arrange
        var promptService = new SystemPromptService();
        var tools = new List<ToolRegistration>();
        
        // Act
        var prompt = promptService.BuildSystemPrompt(tools, "gpt-4o", "openai");
        
        // Assert
        Assert.Contains("For greetings like 'hello', 'hi', 'hey' - just respond conversationally", prompt);
        Assert.Contains("WHEN NOT TO USE TOOLS", prompt);
        Assert.Contains("When the user is just chatting or making small talk", prompt);
    }
    
    [Fact]
    public void SystemPrompt_Should_Include_Tool_Usage_Guidelines()
    {
        // Arrange
        var promptService = new SystemPromptService();
        var tools = new List<ToolRegistration>
        {
            new ToolRegistration
            {
                Metadata = new ToolMetadata
                {
                    Id = "test_tool",
                    Name = "Test Tool",
                    Description = "A test tool",
                    Category = ToolCategory.FileSystem,
                    Parameters = new List<ToolParameter>()
                },
                ToolType = typeof(object)
            }
        };
        
        // Act
        var prompt = promptService.BuildSystemPrompt(tools, "gpt-4o", "cerebras");
        
        // Assert
        Assert.Contains("WHEN TO USE TOOLS", prompt);
        Assert.Contains("When user asks to see/read/explore files or code", prompt);
        Assert.Contains("Only use tools when necessary", prompt);
    }
    
    [Fact]
    public void SystemPrompt_For_Qwen_Should_Have_Special_Instructions()
    {
        // Arrange
        var promptService = new SystemPromptService();
        var tools = new List<ToolRegistration>();
        
        // Act
        var prompt = promptService.BuildSystemPrompt(tools, "qwen-72b", "qwen");
        
        // Assert
        Assert.Contains("CRITICAL INSTRUCTIONS FOR QWEN MODEL", prompt);
        Assert.Contains("For greetings like 'hello' or 'hi', just respond with a greeting - NO TOOLS", prompt);
    }
}