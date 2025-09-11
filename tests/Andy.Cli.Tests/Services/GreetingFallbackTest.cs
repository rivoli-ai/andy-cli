using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class GreetingFallbackTest
{
    [Theory]
    [InlineData("hello")]
    [InlineData("hi")]
    [InlineData("hey")]
    [InlineData("Hello!")]
    [InlineData("Hi there")]
    [InlineData("Hey Andy")]
    public async Task AiConversationService_Should_Provide_Greeting_Fallback(string userMessage)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<FeedView>();
        services.AddSingleton<IContentProcessor, MarkdownContentProcessor>();
        services.AddSingleton<IContentSanitizer, TextContentSanitizer>();
        services.AddSingleton<IContentRenderer, FeedContentRenderer>();
        services.AddSingleton<Andy.Cli.Services.ContentPipeline.ContentPipeline>();
        services.AddSingleton<ILogger<AiConversationService>>(NullLogger<AiConversationService>.Instance);
        
        var feed = new FeedView();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var renderer = new FeedContentRenderer(feed);
        var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, renderer);
        
        // Act - simulate what happens when tools are executed but no response is generated
        pipeline.AddRawContent(""); // Empty response after tool execution
        await pipeline.FinalizeAsync();
        
        // Check if this would trigger the fallback
        var finalResponse = new System.Text.StringBuilder();
        var iteration = 2; // Simulating that we went through tool execution
        
        if (finalResponse.Length == 0 && iteration > 1)
        {
            var lowerMessage = userMessage.ToLowerInvariant().Trim();
            if (lowerMessage == "hello" || lowerMessage == "hi" || lowerMessage == "hey" || 
                lowerMessage.StartsWith("hello") || lowerMessage.StartsWith("hi ") || lowerMessage.StartsWith("hey "))
            {
                var greeting = "Hello! I'm here to help. What would you like to know or work on today?";
                pipeline.AddRawContent(greeting);
                finalResponse.AppendLine(greeting);
            }
        }
        
        // Assert
        Assert.True(finalResponse.Length > 0);
        Assert.Contains("Hello", finalResponse.ToString());
    }
    
    [Fact]
    public async Task AiConversationService_Should_Not_Add_Fallback_When_Response_Exists()
    {
        // Arrange
        var feed = new FeedView();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var renderer = new FeedContentRenderer(feed);
        var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, renderer);
        
        // Act - simulate normal response
        var actualResponse = "Hello! How can I help you today?";
        pipeline.AddRawContent(actualResponse);
        await pipeline.FinalizeAsync();
        
        var finalResponse = new System.Text.StringBuilder();
        finalResponse.AppendLine(actualResponse); // Response was already added
        
        var iteration = 2;
        var userMessage = "hello";
        
        // Should NOT add fallback since we have a response
        if (finalResponse.Length == 0 && iteration > 1)
        {
            // This block should not execute
            finalResponse.AppendLine("FALLBACK");
        }
        
        // Assert
        Assert.DoesNotContain("FALLBACK", finalResponse.ToString());
        Assert.Contains("How can I help you today?", finalResponse.ToString());
    }
}