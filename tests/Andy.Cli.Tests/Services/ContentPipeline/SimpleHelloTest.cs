using System;
using System.Threading.Tasks;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Services.ContentPipeline;

public class SimpleHelloTest
{
    [Fact]
    public async Task Pipeline_Should_Handle_Simple_Hello_Message()
    {
        // Arrange
        var feed = new FeedView();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var renderer = new FeedContentRenderer(feed);
        
        using var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, renderer);
        
        // Act - Simulate processing "hello" as a response
        Exception? caughtException = null;
        try
        {
            // First add user message (like the UI does)
            feed.AddUserMessage("hello");
            
            // Then process LLM response through pipeline
            pipeline.AddRawContent("Hello! How can I help you today?");
            await pipeline.FinalizeAsync();
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }
        
        // Assert
        Assert.Null(caughtException);
    }
    
    [Fact]
    public async Task Pipeline_Should_Handle_Empty_Response()
    {
        // Arrange
        var feed = new FeedView();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var renderer = new FeedContentRenderer(feed);
        
        using var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, renderer);
        
        // Act
        Exception? caughtException = null;
        try
        {
            feed.AddUserMessage("hello");
            pipeline.AddRawContent("");
            await pipeline.FinalizeAsync();
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }
        
        // Assert
        Assert.Null(caughtException);
    }
    
    [Fact]
    public async Task Pipeline_Should_Handle_Mixed_Priority_Content()
    {
        // Arrange
        var feed = new FeedView();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var renderer = new FeedContentRenderer(feed);
        
        using var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, renderer);
        
        // Act
        Exception? caughtException = null;
        try
        {
            // Add content with different priorities
            pipeline.AddRawContent("First content", priority: 100);
            pipeline.AddSystemMessage("System message", SystemMessageType.Context, priority: 1000);
            pipeline.AddRawContent("Second content", priority: 50);
            await pipeline.FinalizeAsync();
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }
        
        // Assert
        Assert.Null(caughtException);
    }
}