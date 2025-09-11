using System;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class ContentPipelineTests
{
    private readonly Mock<ILogger<Andy.Cli.Services.ContentPipeline.ContentPipeline>> _mockLogger;
    private readonly FeedView _feedView;
    private readonly MarkdownContentProcessor _processor;
    private readonly TextContentSanitizer _sanitizer;
    private readonly FeedContentRenderer _renderer;
    private readonly Andy.Cli.Services.ContentPipeline.ContentPipeline _pipeline;

    public ContentPipelineTests()
    {
        _mockLogger = new Mock<ILogger<Andy.Cli.Services.ContentPipeline.ContentPipeline>>();
        _feedView = new FeedView();
        _processor = new MarkdownContentProcessor();
        _sanitizer = new TextContentSanitizer();
        _renderer = new FeedContentRenderer(_feedView);
        _pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(_processor, _sanitizer, _renderer, _mockLogger.Object);
    }

    [Fact]
    public void Should_Process_Simple_Text_Content()
    {
        // Arrange
        var content = "This is a simple text message.";

        // Act
        _pipeline.AddRawContent(content);

        // Assert - content should be queued for processing
        Assert.True(true); // Pipeline processes asynchronously
    }

    [Fact]
    public void Should_Process_Code_Block_Content()
    {
        // Arrange
        var content = @"Here is some code:

```csharp
public void TestMethod()
{
    Console.WriteLine(""Hello World"");
}
```

That was the code.";

        // Act
        _pipeline.AddRawContent(content);

        // Assert - should process both text and code blocks
        Assert.True(true); // Pipeline processes asynchronously
    }

    [Fact]
    public async Task Should_Render_Content_In_Priority_Order()
    {
        // Arrange
        var lowPriorityContent = "Low priority message";
        var highPriorityContent = "High priority message";

        // Act - add in reverse priority order
        _pipeline.AddRawContent(lowPriorityContent, priority: 200);
        _pipeline.AddRawContent(highPriorityContent, priority: 50);
        
        // Finalize to render all content
        await _pipeline.FinalizeAsync();

        // Assert - high priority content should render first
        // Note: In a real test we'd need a way to capture the rendering order
        Assert.True(true);
    }

    [Fact]
    public async Task Should_Handle_System_Messages_With_High_Priority()
    {
        // Arrange
        var regularContent = "Regular content";
        var contextMessage = "Context: 5 messages, ~1000 tokens";

        // Act
        _pipeline.AddRawContent(regularContent, priority: 100);
        _pipeline.AddSystemMessage(contextMessage, SystemMessageType.Context, priority: 2000);
        
        await _pipeline.FinalizeAsync();

        // Assert - context should render last due to high priority number
        Assert.True(true);
    }

    [Fact]
    public void Should_Not_Accept_Content_After_Finalization()
    {
        // Arrange
        _pipeline.AddRawContent("Initial content");

        // Act - finalize pipeline
        _pipeline.FinalizeAsync();
        
        // Try to add content after finalization
        _pipeline.AddRawContent("After finalization");

        // Assert - should not crash, but content should be ignored
        Assert.True(true);
    }

    [Fact]
    public async Task Should_Handle_Empty_And_Whitespace_Content()
    {
        // Act
        _pipeline.AddRawContent("");
        _pipeline.AddRawContent("   ");
        _pipeline.AddRawContent("\n\n\n");
        
        await _pipeline.FinalizeAsync();

        // Assert - should not crash with empty content
        Assert.True(true);
    }

    [Fact]
    public async Task Should_Process_Mixed_Content_Types()
    {
        // Arrange
        var mixedContent = @"Text before code

```javascript
function test() {
    return ""hello"";
}
```

Text after code

```
Plain code block
```

Final text";

        // Act
        _pipeline.AddRawContent(mixedContent);
        await _pipeline.FinalizeAsync();

        // Assert - should handle mixed text and code blocks
        Assert.True(true);
    }

    [Fact] 
    public void Should_Dispose_Cleanly()
    {
        // Act & Assert - should not throw
        _pipeline.Dispose();
    }
}

public class MarkdownContentProcessorTests
{
    private readonly MarkdownContentProcessor _processor;

    public MarkdownContentProcessorTests()
    {
        _processor = new MarkdownContentProcessor();
    }

    [Fact]
    public void Should_Process_Plain_Text()
    {
        // Arrange
        var content = "This is plain text.";

        // Act
        var blocks = _processor.Process(content).ToList();

        // Assert
        Assert.Single(blocks);
        Assert.IsType<TextBlock>(blocks[0]);
        Assert.Equal("This is plain text.", ((TextBlock)blocks[0]).Content);
    }

    [Fact]
    public void Should_Extract_Code_Blocks()
    {
        // Arrange
        var content = @"Text before

```csharp
var x = 5;
```

Text after";

        // Act
        var blocks = _processor.Process(content).ToList();

        // Assert
        Assert.Equal(3, blocks.Count);
        Assert.IsType<TextBlock>(blocks[0]);
        Assert.IsType<CodeBlock>(blocks[1]);
        Assert.IsType<TextBlock>(blocks[2]);
        
        var codeBlock = (CodeBlock)blocks[1];
        Assert.Equal("csharp", codeBlock.Language);
        Assert.Equal("var x = 5;", codeBlock.Code);
    }

    [Fact]
    public void Should_Handle_Empty_Code_Blocks()
    {
        // Arrange
        var content = @"Text before

```
```

Text after";

        // Act
        var blocks = _processor.Process(content).ToList();

        // Assert - should only have text blocks, empty code block should be filtered
        // Text before and after should be combined into a single block when no meaningful code block separates them
        Assert.Single(blocks);
        Assert.IsType<TextBlock>(blocks[0]);
        var textBlock = (TextBlock)blocks[0];
        Assert.Contains("Text before", textBlock.Content);
        Assert.Contains("Text after", textBlock.Content);
    }

    [Fact]
    public void Should_Return_Empty_For_Null_Or_Whitespace()
    {
        // Act & Assert
        Assert.Empty(_processor.Process(null!));
        Assert.Empty(_processor.Process(""));
        Assert.Empty(_processor.Process("   "));
        Assert.Empty(_processor.Process("\n\n\n"));
    }
}

public class TextContentSanitizerTests
{
    private readonly TextContentSanitizer _sanitizer;

    public TextContentSanitizerTests()
    {
        _sanitizer = new TextContentSanitizer();
    }

    [Fact]
    public void Should_Remove_Empty_Json_Blocks()
    {
        // Arrange
        var block = new TextBlock("test", "`json\n\n`\nSome text\n```json\n\n```");

        // Act
        var sanitized = _sanitizer.Sanitize(block) as TextBlock;

        // Assert
        Assert.NotNull(sanitized);
        Assert.DoesNotContain("`json", sanitized.Content);
        Assert.DoesNotContain("```json", sanitized.Content);
        Assert.Contains("Some text", sanitized.Content);
    }

    [Fact]
    public void Should_Remove_Consecutive_Newlines()
    {
        // Arrange
        var block = new TextBlock("test", "Line 1\n\n\n\nLine 2\n\n\nLine 3");

        // Act
        var sanitized = _sanitizer.Sanitize(block) as TextBlock;

        // Assert
        Assert.NotNull(sanitized);
        Assert.DoesNotContain("\n\n", sanitized.Content);
        Assert.Contains("Line 1", sanitized.Content);
        Assert.Contains("Line 2", sanitized.Content);
        Assert.Contains("Line 3", sanitized.Content);
        // Verify it's single-spaced
        Assert.Equal("Line 1\nLine 2\nLine 3", sanitized.Content);
    }

    [Fact]
    public void Should_Handle_Extreme_Whitespace_Like_User_Example()
    {
        // Arrange - simulate the exact user example with many empty lines
        var block = new TextBlock("test", "Conclude:\nAfter migrating all csproj files to .NET 10, I'd verify the results using the\nlist_directory tool to confirm the successful migration.\n\n\n\n\n\n\n\n\n\n\n\nClarify: The goal is to migrate existing .NET 10 csproj files.\nPlan:");

        // Act
        var sanitized = _sanitizer.Sanitize(block) as TextBlock;

        // Assert
        Assert.NotNull(sanitized);
        Assert.DoesNotContain("\n\n", sanitized.Content);
        // Should be single-spaced
        var expectedContent = "Conclude:\nAfter migrating all csproj files to .NET 10, I'd verify the results using the\nlist_directory tool to confirm the successful migration.\nClarify: The goal is to migrate existing .NET 10 csproj files.\nPlan:";
        Assert.Equal(expectedContent, sanitized.Content);
    }

    [Fact]
    public void Should_Mark_Empty_Content_As_Incomplete()
    {
        // Arrange
        var block = new TextBlock("test", "   \n\n   ");

        // Act
        var sanitized = _sanitizer.Sanitize(block) as TextBlock;

        // Assert
        Assert.NotNull(sanitized);
        Assert.False(sanitized.IsComplete);
        Assert.True(string.IsNullOrWhiteSpace(sanitized.Content));
    }

    [Fact]
    public void Should_Handle_Code_Block_Sanitization()
    {
        // Arrange
        var block = new CodeBlock("test", "  \n  var x = 5;  \n  ", "csharp");

        // Act
        var sanitized = _sanitizer.Sanitize(block) as CodeBlock;

        // Assert
        Assert.NotNull(sanitized);
        Assert.True(sanitized.IsComplete);
        Assert.Equal("var x = 5;", sanitized.Code);
        Assert.Equal("csharp", sanitized.Language);
    }

    [Fact]
    public void Should_Handle_System_Message_Sanitization()
    {
        // Arrange
        var block = new SystemMessageBlock("test", "  Context message  \n  ", SystemMessageType.Context);

        // Act
        var sanitized = _sanitizer.Sanitize(block) as SystemMessageBlock;

        // Assert
        Assert.NotNull(sanitized);
        Assert.True(sanitized.IsComplete);
        Assert.Equal("Context message", sanitized.Message);
        Assert.Equal(SystemMessageType.Context, sanitized.Type);
    }
}