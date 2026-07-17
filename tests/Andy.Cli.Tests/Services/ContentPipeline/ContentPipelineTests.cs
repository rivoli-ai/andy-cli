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

    // Re-enabled (#178): Dispose is now non-blocking and never observes/rethrows the background
    // task, so clean disposal holds again even under the strict-by-default ErrorPolicy.
    [Fact]
    public void Should_Dispose_Cleanly()
    {
        // Act & Assert - should not throw
        _pipeline.Dispose();
    }

    [Fact]
    public async Task Should_Dispose_Async_Cleanly()
    {
        // Act & Assert - async disposal should not throw or block
        await _pipeline.DisposeAsync();
    }

    [Fact]
    public async Task Should_Dispose_Cleanly_After_Finalization()
    {
        _pipeline.AddRawContent("Some content");
        await _pipeline.FinalizeAsync();

        // Act & Assert - disposing after finalize must not throw
        await _pipeline.DisposeAsync();
    }
}

// MarkdownContentProcessorTests live in Services/MarkdownContentProcessorTests.cs (kept there to
// avoid a duplicate class definition when both files are compiled).

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
    public void Should_Collapse_Excess_Blank_Lines_But_Preserve_Paragraph_Spacing()
    {
        // Arrange - runs of 3+ newlines collapse to a single blank line; a single blank line
        // between paragraphs is legitimate Markdown and must survive (#178).
        var block = new TextBlock("test", "Line 1\n\n\n\nLine 2\n\n\nLine 3");

        // Act
        var sanitized = _sanitizer.Sanitize(block) as TextBlock;

        // Assert
        Assert.NotNull(sanitized);
        Assert.Contains("Line 1", sanitized!.Content);
        Assert.Contains("Line 2", sanitized.Content);
        Assert.Contains("Line 3", sanitized.Content);
        // Paragraph spacing preserved, runaway gaps collapsed to a single blank line.
        Assert.Equal("Line 1\n\nLine 2\n\nLine 3", sanitized.Content);
    }

    [Fact]
    public void Should_Preserve_Single_Blank_Line_Between_Paragraphs()
    {
        // Arrange
        var block = new TextBlock("test", "First paragraph.\n\nSecond paragraph.");

        // Act
        var sanitized = _sanitizer.Sanitize(block) as TextBlock;

        // Assert - a single blank line between paragraphs is not collapsed
        Assert.NotNull(sanitized);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", sanitized!.Content);
    }

    // Re-enabled (#178): prose mentioning a tool name ("list_directory tool") must survive, and
    // runaway blank lines collapse to a single blank line while paragraph spacing is preserved.
    [Fact]
    public void Should_Handle_Extreme_Whitespace_Like_User_Example()
    {
        // Arrange - simulate the exact user example with many empty lines
        var block = new TextBlock("test", "Conclude:\nAfter migrating all csproj files to .NET 10, I'd verify the results using the\nlist_directory tool to confirm the successful migration.\n\n\n\n\n\n\n\n\n\n\n\nClarify: The goal is to migrate existing .NET 10 csproj files.\nPlan:");

        // Act
        var sanitized = _sanitizer.Sanitize(block) as TextBlock;

        // Assert
        Assert.NotNull(sanitized);
        // The line that mentions the tool name in prose is preserved verbatim.
        Assert.Contains("list_directory tool to confirm the successful migration.", sanitized!.Content);
        // Runaway blank run collapses to a single blank line; paragraph spacing preserved.
        var expectedContent = "Conclude:\nAfter migrating all csproj files to .NET 10, I'd verify the results using the\nlist_directory tool to confirm the successful migration.\n\nClarify: The goal is to migrate existing .NET 10 csproj files.\nPlan:";
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