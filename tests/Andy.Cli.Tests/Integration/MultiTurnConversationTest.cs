using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Services.ContentPipeline;
// Parser-related imports removed
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Integration;

public class MultiTurnConversationTest
{
    [Fact]
    public async Task Should_Display_Response_After_Tool_Execution_In_Second_Turn()
    {
        // This test verifies that when the LLM response contains both tool calls and explanatory text,
        // the text is displayed to the user (not just the tool execution results)
        
        // Arrange
        var feedView = new FeedView();
        var capturedContent = new List<string>();
        
        // Create a test pipeline that captures what gets rendered
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var testRenderer = new TestContentRenderer(capturedContent);
        var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, testRenderer);
        
        // Simulate processing a response that has both text and tool calls
        var responseWithToolsAndText = @"I'll explore the repository structure for you.

{""tool"":""list_directory"",""parameters"":{""path"":"".""}}

Based on the repository structure, this appears to be the Andy CLI project.";
        
        // Act
        // Extract the non-tool text using the same logic as AiConversationService
        var textWithoutTools = ExtractNonToolText(responseWithToolsAndText);
        if (!string.IsNullOrWhiteSpace(textWithoutTools))
        {
            pipeline.AddRawContent(textWithoutTools);
        }
        await pipeline.FinalizeAsync();
        
        // Assert
        Assert.NotEmpty(textWithoutTools);
        Assert.Contains("I'll explore the repository", textWithoutTools);
        Assert.Contains("Based on the repository structure", textWithoutTools);
        Assert.DoesNotContain("tool", textWithoutTools);
        Assert.DoesNotContain("list_directory", textWithoutTools);
        
        // Verify content was rendered
        Assert.NotEmpty(capturedContent);
        Assert.True(capturedContent.Any(c => c.Contains("explore the repository")),
            "Expected explanatory text to be rendered");
    }
    
    // Helper method matching the one in AiConversationService
    private static string ExtractNonToolText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        
        var result = text;
        
        // Remove <tool_call> blocks
        result = System.Text.RegularExpressions.Regex.Replace(result, 
            @"<tool_call>[\s\S]*?</tool_call>", "", 
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        // Remove JSON tool calls
        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"\{[^}]*""tool""\s*:\s*""[^""]+""[^}]*\}", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        // Remove ```json blocks that contain tool calls
        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"```json\s*\n?\s*\{[^}]*""tool""\s*:[^}]*\}\s*\n?\s*```", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        // Clean up extra whitespace
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\n{3,}", "\n\n");
        result = result.Trim();
        
        return result;
    }
    
    [Fact]
    public async Task Should_Continue_Conversation_After_Tool_Execution()
    {
        // This test verifies that after tool execution, if the LLM provides a follow-up response,
        // it gets properly displayed
        
        // Arrange
        var capturedContent = new List<string>();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var testRenderer = new TestContentRenderer(capturedContent);
        var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, testRenderer);
        
        // Simulate the scenario where:
        // 1. First iteration has tool calls
        // 2. Second iteration has the conversational response
        
        var followUpResponse = "Based on the directory listing, this is a .NET project with a CLI application and test suite.";
        
        // Act
        pipeline.AddRawContent(followUpResponse);
        await pipeline.FinalizeAsync();
        
        // Assert
        Assert.NotEmpty(capturedContent);
        Assert.True(capturedContent.Any(c => c.Contains(".NET project")),
            "Expected the follow-up response to be rendered after tool execution");
    }
    
    private class TestContentRenderer : IContentRenderer
    {
        private readonly List<string> _capturedContent;
        
        public TestContentRenderer(List<string> capturedContent)
        {
            _capturedContent = capturedContent;
        }
        
        public void Render(IContentBlock block)
        {
            if (block is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Content))
            {
                _capturedContent.Add(textBlock.Content);
            }
            else if (block is SystemMessageBlock sysBlock && !string.IsNullOrWhiteSpace(sysBlock.Message))
            {
                _capturedContent.Add(sysBlock.Message);
            }
        }
    }
}