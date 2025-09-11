using System;
using System.Collections.Generic;
using Xunit;
using Andy.Cli.Widgets;
using Andy.Cli.Services.ContentPipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.Cli.Tests.Services.ContentPipeline;

public class TypeConversionTests
{
    [Fact]
    public void ToolExecutionItem_HandlesNonStringParameters()
    {
        // Arrange
        var toolId = "test_tool";
        var parameters = new Dictionary<string, object?>
        {
            ["string_param"] = "hello",
            ["int_param"] = 42,
            ["bool_param"] = true,
            ["null_param"] = null,
            ["object_param"] = new { nested = "value" },
            ["array_param"] = new[] { 1, 2, 3 }
        };
        var result = "Success";
        
        // Act - This should not throw
        var item = new ToolExecutionItem(toolId, parameters, result, true);
        
        // Assert - Measure line count to ensure it rendered
        var lineCount = item.MeasureLineCount(80);
        Assert.True(lineCount > 0);
    }

    [Fact]
    public void FeedView_AddToolExecution_HandlesNonStringParameters()
    {
        // Arrange
        var feed = new FeedView();
        var toolId = "test_tool";
        var parameters = new Dictionary<string, object?>
        {
            ["string_param"] = "hello",
            ["int_param"] = 42,
            ["bool_param"] = true,
            ["null_param"] = null,
            ["object_param"] = new { nested = "value" },
            ["array_param"] = new[] { 1, 2, 3 }
        };
        var result = "Success";
        
        // Act - This should not throw
        Exception? caughtException = null;
        try
        {
            feed.AddToolExecution(toolId, parameters, result, true);
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }
        
        // Assert - No exception should be thrown
        Assert.Null(caughtException);
    }

    [Fact]
    public void ContentPipeline_ProcessesSystemMessageBlock()
    {
        // Arrange
        var feed = new FeedView();
        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var renderer = new FeedContentRenderer(feed);
        
        using var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, renderer);
        
        // Act
        pipeline.AddSystemMessage("Test message", SystemMessageType.Context);
        pipeline.FinalizeAsync().Wait();
        
        // Assert - Just verify no exception is thrown
        Assert.True(true);
    }

    [Fact]
    public void StringComparison_WithMixedTypes_DoesNotThrow()
    {
        // This tests a scenario that might cause "Object must be of type String" error
        object value1 = "hello";
        object value2 = 42;
        
        // Test ToString conversions
        var str1 = value1?.ToString();
        var str2 = value2?.ToString();
        
        Assert.Equal("hello", str1);
        Assert.Equal("42", str2);
        
        // Test that comparison doesn't throw
        Assert.NotEqual(str1, str2);
    }

    [Fact]
    public void DictionaryWithMixedTypes_CanBeIterated()
    {
        // Arrange
        var dict = new Dictionary<string, object?>
        {
            ["key1"] = "string value",
            ["key2"] = 123,
            ["key3"] = true,
            ["key4"] = null
        };
        
        // Act & Assert - Should not throw
        foreach (var kvp in dict)
        {
            var key = kvp.Key;
            var value = kvp.Value?.ToString() ?? "null";
            Assert.NotNull(key);
            Assert.NotNull(value);
        }
    }
}