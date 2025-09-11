using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Andy.Cli.Services.ContentPipeline;

namespace Andy.Cli.Tests.Services.ContentPipeline;

public class OrderingTests
{
    [Fact]
    public void OrderBy_WithMixedIds_ShouldNotThrow()
    {
        // Arrange
        var blocks = new List<IContentBlock>
        {
            new TextBlock("text_1", "Content 1", 100),
            new TextBlock("system_Context_1234567890", "System content", 1000),
            new CodeBlock("block_2", "code", "csharp", 100),
            new SystemMessageBlock("system_Info_9876543210", "Info", SystemMessageType.Info, 900),
            new TextBlock("", "Empty ID", 100), // Edge case: empty string ID
            new TextBlock("123", "Numeric ID", 100), // Edge case: numeric string
        };
        
        // Act - This should not throw
        Exception? caughtException = null;
        List<IContentBlock>? sorted = null;
        try
        {
            sorted = blocks
                .OrderBy(b => b.Priority)
                .ThenBy(b => b.Id)
                .ToList();
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }
        
        // Assert
        Assert.Null(caughtException);
        Assert.NotNull(sorted);
        Assert.Equal(blocks.Count, sorted!.Count);
    }

    [Fact]
    public void OrderBy_WithNullableComparison_ShouldNotThrow()
    {
        // This tests a potential issue with string comparison
        var items = new[]
        {
            new { Id = "abc", Priority = 1 },
            new { Id = "123", Priority = 2 },
            new { Id = "", Priority = 1 },
            new { Id = "xyz_456", Priority = 1 }
        };
        
        // Act
        var sorted = items
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
        
        // Assert
        Assert.Equal(3, sorted.Count(x => x.Priority == 1));
        Assert.Equal(1, sorted.Count(x => x.Priority == 2));
    }

    [Fact]
    public void ContentPipeline_Ordering_ShouldNotThrow()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var renderer = new TestRenderer();
        
        using var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, renderer);
        
        // Add various content types
        pipeline.AddRawContent("First text content");
        pipeline.AddSystemMessage("System message", SystemMessageType.Context);
        pipeline.AddRawContent("```csharp\ncode here\n```");
        pipeline.AddSystemMessage("Another system message", SystemMessageType.Info, 500);
        
        // Act - Finalize should trigger ordering
        Exception? caughtException = null;
        try
        {
            pipeline.FinalizeAsync().Wait();
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }
        
        // Assert
        Assert.Null(caughtException);
        Assert.True(renderer.RenderedBlocks.Count > 0);
    }
    
    private class TestRenderer : IContentRenderer
    {
        public List<IContentBlock> RenderedBlocks { get; } = new();
        
        public void Render(IContentBlock block)
        {
            RenderedBlocks.Add(block);
        }
    }
}