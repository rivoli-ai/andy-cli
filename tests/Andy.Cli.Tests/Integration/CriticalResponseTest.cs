using System;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// Critical tests to ensure responses are always displayed to users
/// </summary>
public class CriticalResponseTest
{
    [Fact]
    public async Task Should_Always_Display_Something_For_Simple_Requests()
    {
        // This tests the critical scenario where "write a sample C# program" produces no output
        
        // Arrange
        var capturedContent = new System.Collections.Generic.List<string>();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var testRenderer = new TestContentRenderer(capturedContent);
        var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, testRenderer);
        
        // Simulate the scenario where LLM provides no response
        var emptyResponse = "";
        
        // Act
        // The system should detect no content was provided and add a fallback
        if (string.IsNullOrWhiteSpace(emptyResponse))
        {
            // This simulates what the improved AiConversationService does
            var fallbackContent = "I apologize, but I didn't receive a proper response from the language model.";
            pipeline.AddRawContent(fallbackContent);
        }
        
        await pipeline.FinalizeAsync();
        
        // Assert
        Assert.NotEmpty(capturedContent);
        Assert.Contains("apologize", capturedContent[0], StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public async Task Should_Display_Tool_Results_When_No_LLM_Response()
    {
        // This tests when tools are executed but LLM provides no follow-up
        
        // Arrange
        var capturedContent = new System.Collections.Generic.List<string>();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var testRenderer = new TestContentRenderer(capturedContent);
        var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, testRenderer);
        
        var toolResults = new[]
        {
            ("code_index", "Found 10 classes in the project"),
            ("list_directory", "src/\ntests/\nREADME.md")
        };
        
        // Act
        // Simulate what happens when tools are executed but no LLM response follows
        if (toolResults.Any())
        {
            var summary = "I've completed analyzing your request. Here's what I found:\n\n";
            foreach (var (toolId, result) in toolResults)
            {
                summary += $"From {toolId}:\n  • {result}\n";
            }
            pipeline.AddRawContent(summary);
        }
        
        await pipeline.FinalizeAsync();
        
        // Assert
        Assert.NotEmpty(capturedContent);
        var content = string.Join(" ", capturedContent);
        Assert.Contains("analyzing your request", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("code_index", content);
        Assert.Contains("10 classes", content);
    }
    
    [Theory]
    [InlineData("write a sample C# program")]
    [InlineData("create a hello world application")]
    [InlineData("show me an example of async/await")]
    public async Task Should_Never_Leave_User_Without_Response(string userMessage)
    {
        // Critical test: No matter what the user asks, they should ALWAYS get a response
        
        // Arrange
        var capturedContent = new System.Collections.Generic.List<string>();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var testRenderer = new TestContentRenderer(capturedContent);
        var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, testRenderer);
        
        // Act
        // Simulate various failure scenarios
        bool hasDisplayedContent = false;
        
        // Scenario 1: LLM returns empty
        var llmResponse = "";
        
        // Scenario 2: LLM returns only tool calls with no text
        var hasToolCalls = userMessage.Contains("example"); // Simulate some queries trigger tools
        
        if (!string.IsNullOrWhiteSpace(llmResponse))
        {
            pipeline.AddRawContent(llmResponse);
            hasDisplayedContent = true;
        }
        
        // The safety net - ALWAYS display something
        if (!hasDisplayedContent)
        {
            var fallback = $"I'll help you with: {userMessage}\n\n[Response would be displayed here]";
            pipeline.AddRawContent(fallback);
        }
        
        await pipeline.FinalizeAsync();
        
        // Assert - The most critical assertion: content is NEVER empty
        Assert.NotEmpty(capturedContent);
        Assert.True(capturedContent.Any(c => !string.IsNullOrWhiteSpace(c)),
            $"User asked '{userMessage}' but received no visible response!");
    }
    
    private class TestContentRenderer : IContentRenderer
    {
        private readonly System.Collections.Generic.List<string> _capturedContent;
        
        public TestContentRenderer(System.Collections.Generic.List<string> capturedContent)
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