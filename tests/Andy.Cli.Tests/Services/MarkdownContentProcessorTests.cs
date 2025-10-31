using Andy.Cli.Services.ContentPipeline;

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

    [Fact]
    public void Process_DoesNotDuplicateCodeBlocks()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = @"# Installation

Build and run using .NET commands:

```bash
dotnet build
dotnet run --project src/Andy.Cli
```

## Configuration

Set environment variables:

```bash
export OPENAI_API_KEY=""your-key""
```

Done!";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        // Should have: text, code, text, code, text = 5 blocks
        Assert.Equal(5, blocks.Count);

        // Verify first text block
        Assert.IsType<TextBlock>(blocks[0]);
        var text1 = (TextBlock)blocks[0];
        Assert.Contains("Installation", text1.Content);
        Assert.Contains("Build and run", text1.Content);

        // Verify first code block
        Assert.IsType<CodeBlock>(blocks[1]);
        var code1 = (CodeBlock)blocks[1];
        Assert.Equal("bash", code1.Language);
        Assert.Contains("dotnet build", code1.Code);
        Assert.Contains("dotnet run", code1.Code);
        // Ensure code block doesn't contain text from surrounding content
        Assert.DoesNotContain("Installation", code1.Code);
        Assert.DoesNotContain("Configuration", code1.Code);

        // Verify second text block
        Assert.IsType<TextBlock>(blocks[2]);
        var text2 = (TextBlock)blocks[2];
        Assert.Contains("Configuration", text2.Content);
        Assert.Contains("Set environment", text2.Content);

        // Verify second code block
        Assert.IsType<CodeBlock>(blocks[3]);
        var code2 = (CodeBlock)blocks[3];
        Assert.Equal("bash", code2.Language);
        Assert.Contains("export OPENAI_API_KEY", code2.Code);
        // Ensure no duplication from first code block
        Assert.DoesNotContain("dotnet build", code2.Code);

        // Verify third text block
        Assert.IsType<TextBlock>(blocks[4]);
        var text3 = (TextBlock)blocks[4];
        Assert.Contains("Done!", text3.Content);
    }

    [Fact]
    public void Process_PreservesContentOrder()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = @"First

```
code1
```

Middle

```
code2
```

Last";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        Assert.Equal(5, blocks.Count);

        // Verify order
        var text1 = blocks[0] as TextBlock;
        var code1 = blocks[1] as CodeBlock;
        var text2 = blocks[2] as TextBlock;
        var code2 = blocks[3] as CodeBlock;
        var text3 = blocks[4] as TextBlock;

        Assert.NotNull(text1);
        Assert.NotNull(code1);
        Assert.NotNull(text2);
        Assert.NotNull(code2);
        Assert.NotNull(text3);

        Assert.Contains("First", text1.Content);
        Assert.Contains("code1", code1.Code);
        Assert.Contains("Middle", text2.Content);
        Assert.Contains("code2", code2.Code);
        Assert.Contains("Last", text3.Content);
    }

    [Fact]
    public void Process_HandlesRealWorldReadmeExample()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = @"## Installation

Build and run using .NET commands:

```bash
dotnet build
dotnet run --project src/Andy.Cli
```

## Commands

### Interactive Mode (TUI)

Run without arguments to start the interactive terminal interface:

```bash
dotnet run --project src/Andy.Cli
```

### Command Line Mode

```bash
# Model management
dotnet run --project src/Andy.Cli -- model list
dotnet run --project src/Andy.Cli -- model switch openai
```";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        // Should have alternating text and code blocks
        Assert.True(blocks.Count >= 5, $"Expected at least 5 blocks, got {blocks.Count}");

        // Verify no duplication by checking each code block is unique
        var codeBlocks = blocks.OfType<CodeBlock>().ToList();
        Assert.Equal(3, codeBlocks.Count);

        // First code block
        Assert.Contains("dotnet build", codeBlocks[0].Code);
        Assert.Contains("dotnet run --project src/Andy.Cli", codeBlocks[0].Code);
        Assert.DoesNotContain("model list", codeBlocks[0].Code);

        // Second code block
        Assert.Contains("dotnet run --project src/Andy.Cli", codeBlocks[1].Code);
        Assert.DoesNotContain("dotnet build", codeBlocks[1].Code);
        Assert.DoesNotContain("model list", codeBlocks[1].Code);

        // Third code block
        Assert.Contains("model list", codeBlocks[2].Code);
        Assert.Contains("model switch", codeBlocks[2].Code);
        Assert.DoesNotContain("dotnet build", codeBlocks[2].Code);
    }

    [Fact]
    public void Process_HandlesLlmResponseWithMarkdownCodeBlock()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();

        // This is the actual response from LLM when asked about README.md
        var llmResponse = @"Here's the content of the `README.md` file:

```markdown
# andy-cli
Command line AI code assistant powered by .NET 8

## Features
- **Interactive TUI** - Modern terminal interface
- **Multi-Provider Support** - Works with OpenAI, Cerebras, Azure OpenAI, and Ollama

## Installation
```bash
dotnet build
dotnet run --project src/Andy.Cli
```

## Configuration
### Environment Variables
- `OPENAI_API_KEY` - OpenAI API key
- `AZURE_OPENAI_API_KEY` - Azure OpenAI API key
```

If you need more details, feel free to ask!";

        // Act
        var blocks = processor.Process(llmResponse).ToList();

        // Assert
        // After recursively processing the markdown code block, we should get:
        // 1. Text block: "Here's the content..."
        // 2. Text block: markdown content before first code block (titles, features, installation header)
        // 3. Code block: bash code from the Installation section
        // 4. Text block: markdown content after code block (Configuration section)
        // 5. Text block: "If you need more details..."
        Assert.Equal(5, blocks.Count);

        // First block: intro text
        Assert.IsType<TextBlock>(blocks[0]);
        var introText = (TextBlock)blocks[0];
        Assert.Contains("Here's the content", introText.Content);

        // Second block: markdown content before bash code block
        Assert.IsType<TextBlock>(blocks[1]);
        var markdownText1 = (TextBlock)blocks[1];
        Assert.Contains("# andy-cli", markdownText1.Content);
        Assert.Contains("## Features", markdownText1.Content);
        Assert.Contains("## Installation", markdownText1.Content);

        // Third block: bash code block (extracted from the markdown)
        Assert.IsType<CodeBlock>(blocks[2]);
        var bashCode = (CodeBlock)blocks[2];
        Assert.Equal("bash", bashCode.Language);
        Assert.Contains("dotnet build", bashCode.Code);
        Assert.Contains("dotnet run --project src/Andy.Cli", bashCode.Code);

        // Fourth block: markdown content after bash code block
        Assert.IsType<TextBlock>(blocks[3]);
        var markdownText2 = (TextBlock)blocks[3];
        Assert.Contains("## Configuration", markdownText2.Content);
        Assert.Contains("Environment Variables", markdownText2.Content);

        // Fifth block: closing text
        Assert.IsType<TextBlock>(blocks[4]);
        var closingText = (TextBlock)blocks[4];
        Assert.Contains("If you need more details", closingText.Content);
    }

    [Fact]
    public void Process_HandlesNestedCodeBlocksInMarkdownCodeBlock()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();
        var markdown = @"Here's some markdown:

```markdown
# Title

## Section
Some text here.

```bash
echo ""hello""
```

More text.
```

Done!";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        // After recursively processing the markdown code block, we should get:
        // 1. Text block: "Here's some markdown:"
        // 2. Text block: markdown content before bash code (Title, Section, Some text here)
        // 3. Code block: bash code (echo "hello")
        // 4. Text block: markdown content after bash code (More text)
        // 5. Text block: "Done!"
        Assert.Equal(5, blocks.Count);

        // First: intro text
        Assert.IsType<TextBlock>(blocks[0]);
        var introText = (TextBlock)blocks[0];
        Assert.Contains("Here's some markdown", introText.Content);

        // Second: markdown content before bash code
        Assert.IsType<TextBlock>(blocks[1]);
        var markdownText1 = (TextBlock)blocks[1];
        Assert.Contains("# Title", markdownText1.Content);
        Assert.Contains("## Section", markdownText1.Content);
        Assert.Contains("Some text here", markdownText1.Content);

        // Third: bash code block (extracted from the markdown)
        Assert.IsType<CodeBlock>(blocks[2]);
        var bashCode = (CodeBlock)blocks[2];
        Assert.Equal("bash", bashCode.Language);
        Assert.Contains("echo", bashCode.Code);

        // Fourth: markdown content after bash code
        Assert.IsType<TextBlock>(blocks[3]);
        var markdownText2 = (TextBlock)blocks[3];
        Assert.Contains("More text", markdownText2.Content);

        // Fifth: closing text
        Assert.IsType<TextBlock>(blocks[4]);
        var closingText = (TextBlock)blocks[4];
        Assert.Contains("Done!", closingText.Content);
    }

    [Fact]
    public void Process_HandlesDirectMarkdownWithoutWrapper()
    {
        // Arrange
        var processor = new MarkdownContentProcessor();

        // This is the case that renders correctly - direct markdown without ```markdown``` wrapper
        var markdown = @"The `README.md` file for the `andy-cli` project contains the following information:

### andy-cli
Command line AI code assistant powered by .NET 8

**ALPHA RELEASE WARNING**

This software is in ALPHA stage. **NO GUARANTEES** are made about its functionality, stability, or safety.

**CRITICAL WARNINGS:**
- This tool performs **DESTRUCTIVE OPERATIONS** on files and directories
- Permission management is **NOT FULLY TESTED** and may have security vulnerabilities
- **DO NOT USE** in production environments

### Features
- **Interactive TUI** - Modern terminal interface with real-time streaming responses
- **Multi-Provider Support** - Works with OpenAI, Cerebras, Azure OpenAI, and Ollama
- **Smart Provider Detection** - Automatically selects the best available LLM provider

### Installation
```bash
dotnet build
dotnet run --project src/Andy.Cli
```

### Configuration
- **Automatic Provider Detection**: Detects and selects the best available LLM provider based on environment variables.
- **Environment Variables**: Required for providers like OpenAI, Azure OpenAI, Cerebras, and Ollama.

### Commands
- **Interactive Mode (TUI)**: Run without arguments to start the interactive terminal interface.
- **Command Line Mode**: Manage models and tools via command line.

This README provides a comprehensive overview of the project's features and warnings about its alpha status.";

        // Act
        var blocks = processor.Process(markdown).ToList();

        // Assert
        // Should create:
        // 1. Text block: everything before code block (intro, headers, features, installation header)
        // 2. Code block: bash code
        // 3. Text block: everything after code block (configuration, commands, closing text)
        Assert.Equal(3, blocks.Count);

        // First block: text before code
        Assert.IsType<TextBlock>(blocks[0]);
        var textBefore = (TextBlock)blocks[0];
        Assert.Contains("README.md", textBefore.Content);
        Assert.Contains("### andy-cli", textBefore.Content);
        Assert.Contains("ALPHA RELEASE WARNING", textBefore.Content);
        Assert.Contains("### Features", textBefore.Content);
        Assert.Contains("### Installation", textBefore.Content);

        // Second block: bash code
        Assert.IsType<CodeBlock>(blocks[1]);
        var bashCode = (CodeBlock)blocks[1];
        Assert.Equal("bash", bashCode.Language);
        Assert.Contains("dotnet build", bashCode.Code);
        Assert.Contains("dotnet run --project src/Andy.Cli", bashCode.Code);

        // Third block: text after code
        Assert.IsType<TextBlock>(blocks[2]);
        var textAfter = (TextBlock)blocks[2];
        Assert.Contains("### Configuration", textAfter.Content);
        Assert.Contains("### Commands", textAfter.Content);
        Assert.Contains("comprehensive overview", textAfter.Content);
    }
}
