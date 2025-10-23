using Andy.Cli.Services.ContentPipeline;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class MarkdownContentProcessorTests
{
    [Fact]
    public void Process_ExtractsCodeBlockWithLanguage()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = @"Some text before

```bash
dotnet build
dotnet run --project src/Andy.Cli
```

Some text after";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        Assert.Equal(3, blocks.Count);

        // First block: text before
        Assert.IsType<TextBlock>(blocks[0]);
        var textBlock1 = (TextBlock)blocks[0];
        Assert.Contains("Some text before", textBlock1.Content);

        // Second block: code
        Assert.IsType<CodeBlock>(blocks[1]);
        var codeBlock = (CodeBlock)blocks[1];
        Assert.Equal("bash", codeBlock.Language);
        Assert.Contains("dotnet build", codeBlock.Code);
        Assert.Contains("dotnet run", codeBlock.Code);

        // Third block: text after
        Assert.IsType<TextBlock>(blocks[2]);
        var textBlock2 = (TextBlock)blocks[2];
        Assert.Contains("Some text after", textBlock2.Content);
    }

    [Fact]
    public void Process_ExtractsCodeBlockWithoutLanguage()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = @"```
some code
more code
```";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        Assert.Single(blocks);
        Assert.IsType<CodeBlock>(blocks[0]);
        var codeBlock = (CodeBlock)blocks[0];
        Assert.Null(codeBlock.Language);
        Assert.Contains("some code", codeBlock.Code);
    }

    [Fact]
    public void Process_HandlesMultipleCodeBlocks()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = @"First section

```csharp
var x = 1;
```

Middle section

```javascript
const y = 2;
```

Last section";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        Assert.Equal(5, blocks.Count);
        Assert.IsType<TextBlock>(blocks[0]);
        Assert.IsType<CodeBlock>(blocks[1]);
        Assert.IsType<TextBlock>(blocks[2]);
        Assert.IsType<CodeBlock>(blocks[3]);
        Assert.IsType<TextBlock>(blocks[4]);

        var codeBlock1 = (CodeBlock)blocks[1];
        Assert.Equal("csharp", codeBlock1.Language);
        Assert.Contains("var x = 1;", codeBlock1.Code);

        var codeBlock2 = (CodeBlock)blocks[3];
        Assert.Equal("javascript", codeBlock2.Language);
        Assert.Contains("const y = 2;", codeBlock2.Code);
    }

    [Fact]
    public void Process_HandlesCodeBlockWithExtraWhitespace()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = "```bash  \n  dotnet build  \n  dotnet test  \n```";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        Assert.Single(blocks);
        Assert.IsType<CodeBlock>(blocks[0]);
        var codeBlock = (CodeBlock)blocks[0];
        Assert.Equal("bash", codeBlock.Language);
        Assert.Contains("dotnet build", codeBlock.Code);
        Assert.Contains("dotnet test", codeBlock.Code);
    }

    [Fact]
    public void Process_HandlesCodeBlockWithWindowsLineEndings()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = "```python\r\nprint('hello')\r\nprint('world')\r\n```";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        Assert.Single(blocks);
        Assert.IsType<CodeBlock>(blocks[0]);
        var codeBlock = (CodeBlock)blocks[0];
        Assert.Equal("python", codeBlock.Language);
        Assert.Contains("print('hello')", codeBlock.Code);
        Assert.Contains("print('world')", codeBlock.Code);
    }

    [Fact]
    public void Process_FiltersOutEmptyContent()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = "   \n\n   ";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        Assert.Empty(blocks);
    }

    [Fact]
    public void Process_HandlesPlainTextOnly()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = "Just some plain text with no code blocks";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        Assert.Single(blocks);
        Assert.IsType<TextBlock>(blocks[0]);
        var textBlock = (TextBlock)blocks[0];
        Assert.Equal("Just some plain text with no code blocks", textBlock.Content);
    }
}
