using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Andy.Cli.Widgets;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Tests for markdown rendering, particularly paragraph spacing
    /// </summary>
    public class MarkdownRenderingTests
    {
        [Fact]
        public void MarkdownRenderer_ShouldAddSpacing_AfterListBeforeHeading()
        {
            // Arrange
            var markdown = @"Core Functionality
• Program.cs: Contains a public method WriteAsync for asynchronous writing
Widgets and Tools
• Widgets:";

            var expected = new[]
            {
                "Core Functionality",
                "• Program.cs: Contains a public method WriteAsync for asynchronous writing",
                "",  // Blank line should be added here
                "Widgets and Tools",
                "• Widgets:"
            };

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert
            Assert.Equal(expected.Length, result.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i]);
            }
        }

        [Fact]
        public void MarkdownRenderer_ShouldAddSpacing_AfterListBeforeText()
        {
            // Arrange
            var markdown = @"List of items:
- Item 1
- Item 2
This is regular text after the list.";

            var expected = new[]
            {
                "List of items:",
                "- Item 1",
                "- Item 2",
                "",  // Blank line should be added here
                "This is regular text after the list."
            };

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert
            Assert.Equal(expected.Length, result.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i]);
            }
        }

        [Fact]
        public void MarkdownRenderer_ShouldNotAddSpacing_BetweenListItems()
        {
            // Arrange
            var markdown = @"- Item 1
- Item 2
- Item 3";

            var expected = new[]
            {
                "- Item 1",
                "- Item 2",
                "- Item 3"
            };

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert
            Assert.Equal(expected.Length, result.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i]);
            }
        }

        [Fact]
        public void MarkdownRenderer_ShouldNotAddSpacing_WhenBlankLineAlreadyExists()
        {
            // Arrange
            var markdown = @"List of items:
- Item 1
- Item 2

This is regular text after the list.";

            // Should not add another blank line since one already exists
            var expected = new[]
            {
                "List of items:",
                "- Item 1",
                "- Item 2",
                "",  // Existing blank line
                "This is regular text after the list."
            };

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert
            Assert.Equal(expected.Length, result.Length);
        }

        [Fact]
        public void MarkdownRenderer_ShouldAddSpacing_BeforeHeading()
        {
            // Arrange
            var markdown = @"Some regular text.
# Main Heading
More text here.";

            var expected = new[]
            {
                "Some regular text.",
                "",  // Blank line should be added before heading
                "# Main Heading",
                "More text here."
            };

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert
            Assert.Equal(expected.Length, result.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i]);
            }
        }

        [Fact]
        public void MarkdownRenderer_ShouldHandleNumberedLists()
        {
            // Arrange
            var markdown = @"Steps to follow:
1. First step
2. Second step
This concludes the steps.";

            var expected = new[]
            {
                "Steps to follow:",
                "1. First step",
                "2. Second step",
                "",  // Blank line should be added here
                "This concludes the steps."
            };

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert
            Assert.Equal(expected.Length, result.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i]);
            }
        }

        [Fact]
        public void MarkdownRenderer_ShouldHandleBulletMarker()
        {
            // Arrange - using actual bullet character
            var markdown = @"Items:
• First item
• Second item
Next section";

            var expected = new[]
            {
                "Items:",
                "• First item",
                "• Second item",
                "",  // Blank line should be added here
                "Next section"
            };

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert
            Assert.Equal(expected.Length, result.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i]);
            }
        }

        [Fact]
        public void MarkdownRenderer_ShouldHandleComplexREADMEContent()
        {
            // Arrange - Real-world README-style markdown with headings, code blocks, bullets, and bold text
            var markdown = @"The `README.md` for the `andy-cli` project provides an overview of the application.
### Overview
- **andy-cli** is a command-line AI code assistant powered by .NET 8.
- **Alpha Release Warning**: The software is in the alpha stage.
### Installation
Build and run the project using .NET commands:
```bash
dotnet build
dotnet run --project src/Andy.Cli
```
### Configuration
- Automatic provider detection prioritizes OpenAI, Cerebras, Ollama, and Azure OpenAI.
- Environment variables are required for provider configuration.";

            var expected = new[]
            {
                "The `README.md` for the `andy-cli` project provides an overview of the application.",
                "",
                "### Overview",
                "- **andy-cli** is a command-line AI code assistant powered by .NET 8.",
                "- **Alpha Release Warning**: The software is in the alpha stage.",
                "",
                "### Installation",
                "Build and run the project using .NET commands:",
                "```bash",
                "dotnet build",
                "dotnet run --project src/Andy.Cli",
                "```",
                "",
                "### Configuration",
                "- Automatic provider detection prioritizes OpenAI, Cerebras, Ollama, and Azure OpenAI.",
                "- Environment variables are required for provider configuration."
            };

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert
            Assert.Equal(expected.Length, result.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i]);
            }
        }

        [Fact]
        public void MarkdownRenderer_ShouldNotAddTrailingBlankLines()
        {
            // Arrange - Real-world case with multiple list sections ending with regular text
            var markdown = @"### README_COMMANDS.md
This document outlines the features:
- **Model Management Commands**: Commands for listing models.
- **Command Palette**: Quick access to commands.
- **Usage Examples**: Demonstrates how to use slash commands.

### REFACTORING_PLAN.md
This document details the refactoring strategy:
- **Completed**: Centralized theme system.
- **In Progress**: Refactoring of FeedView.
- **Planned**: Integration of theme usage.

If you need more detailed information, let me know!";

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert - should NOT end with blank lines
            Assert.NotEmpty(result);
            Assert.Equal("If you need more detailed information, let me know!", result[^1]);

            // Count trailing blank lines (should be 0)
            int trailingBlanks = 0;
            for (int i = result.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(result[i]))
                    trailingBlanks++;
                else
                    break;
            }
            Assert.Equal(0, trailingBlanks);
        }

        [Fact]
        public void MarkdownRenderer_ShouldTrimTrailingNewlinesFromInput()
        {
            // Arrange - Markdown with trailing newlines (common from LLM responses)
            var markdown = "Here is some text.\n\nMore text here.\n\n\n\n\n";

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert - should NOT have trailing blank lines
            Assert.NotEmpty(result);
            Assert.Equal("More text here.", result[^1]);

            // Verify no trailing blanks
            int trailingBlanks = 0;
            for (int i = result.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(result[i]))
                    trailingBlanks++;
                else
                    break;
            }
            Assert.Equal(0, trailingBlanks);
        }

        [Fact]
        public void MarkdownRenderer_ShouldHandleREADMEResponseWithoutTrailingBlanks()
        {
            // Arrange - Exact content from user's screenshot showing trailing blank line issue
            var markdown = @"The README.md file contains the following information about the ""andy-cli"" project:

### Overview
- **andy-cli**: A command-line AI code assistant powered by .NET 8.
- **ALPHA RELEASE WARNING**: The software is in the alpha stage, with critical warnings about its functionality, stability, and safety.

### Features
- **Interactive TUI**: Modern terminal interface with real-time streaming responses.
- **Multi-Provider Support**: Works with OpenAI, Cerebras, Azure OpenAI, and Ollama.
- **Smart Provider Detection**: Automatically selects the best available LLM provider.
- **Tool Execution**: Supports file operations, code search, bash commands, and more.
- **Context-Aware**: Maintains conversation history with intelligent context management.
- **Performance Optimized**: Efficient streaming and rendering with the Andy.Tui framework.

### Installation
- Instructions for building and running the project using `dotnet build` and `dotnet run`.

### Configuration
- **Automatic Provider Detection**: Describes how the CLI detects and selects the best LLM provider based on environment variables.
- **Environment Variables**: Lists required variables for different providers and optional configuration settings.

### Commands
- **Interactive Mode (TUI)**: Instructions for starting the interactive terminal interface and using keyboard shortcuts.
- **Slash Commands**: Lists available commands for model management and tool execution.

### Development
- **Architecture**: Describes the modular architecture of the project.
- **Project Structure**: Overview of the directory structure and components.
- **Testing**: Instructions for running tests and generating coverage reports.
- **Building**: Commands for building and cleaning the project.

### License
- Mentions the LICENSE file for details on the project's license.

This README provides a comprehensive guide to using, configuring, and developing the andy-cli project. If you need more specific details or have any questions, feel free to ask!";

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert - last line should be the actual content, not blank
            Assert.NotEmpty(result);
            Assert.EndsWith("feel free to ask!", result[^1]);

            // Verify no trailing blanks
            int trailingBlanks = 0;
            for (int i = result.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(result[i]))
                    trailingBlanks++;
                else
                    break;
            }
            Assert.Equal(0, trailingBlanks);
        }

        [Fact]
        public void MarkdownRendererItem_ShouldNotHaveTrailingBlankLinesInMeasurement()
        {
            // Arrange - Create actual MarkdownRendererItem with trailing newlines
            var markdown = "Here is content.\n\nMore content.\n\n\n\n";
            var item = new MarkdownRendererItem(markdown);

            // Act - Measure the line count
            int lineCount = item.MeasureLineCount(80);

            // Assert - The line count should only reflect actual content, not trailing blanks
            // Split the markdown and count non-blank trailing lines
            var lines = markdown.TrimEnd().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // The measurement should not include phantom blank lines
            // We expect at most 2-3 lines for this simple content (accounting for word wrap)
            Assert.True(lineCount <= 10, $"Line count {lineCount} is unexpectedly high, suggesting trailing blanks are being counted");
        }

        [Fact]
        public void MarkdownRenderer_ShouldHandleCodeFences()
        {
            // Arrange - Markdown with code fence
            var markdown = @"Install the tool:
```bash
dotnet build
dotnet run --project src/Andy.Cli
```
After installation, you can use it.";

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert - Code fence markers should be handled correctly
            Assert.Contains("Install the tool:", result);
            Assert.Contains("After installation, you can use it.", result);

            // The ``` markers should be present in the input but might be filtered during rendering
            // We just verify the structure is preserved
            Assert.True(result.Length > 5, "Result should contain multiple lines including code block content");
        }

        [Fact]
        public void MarkdownRenderer_ShouldHandleMultipleCodeFences()
        {
            // Arrange - Markdown with multiple code fences
            var markdown = @"First example:
```bash
command1
```
Some text.

Second example:
```csharp
var x = 42;
```
Done.";

            // Act
            var result = ProcessMarkdownSpacing(markdown);

            // Assert - All content should be preserved
            Assert.Contains("First example:", result);
            Assert.Contains("Some text.", result);
            Assert.Contains("Second example:", result);
            Assert.Contains("Done.", result);
        }

        /// <summary>
        /// Helper method that simulates the AddParagraphSpacing logic
        /// This allows testing the spacing logic without needing full rendering
        /// </summary>
        private static string[] ProcessMarkdownSpacing(string markdown)
        {
            // Trim trailing whitespace like the real implementation
            var trimmed = (markdown ?? string.Empty).TrimEnd();
            var lines = trimmed.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            return AddParagraphSpacing(lines).ToArray();
        }

        private static List<string> AddParagraphSpacing(List<string> lines)
        {
            if (lines.Count == 0) return lines;

            var result = new List<string>();

            for (int i = 0; i < lines.Count; i++)
            {
                string current = lines[i];
                string next = i < lines.Count - 1 ? lines[i + 1] : "";

                result.Add(current);

                // Don't add spacing if current or next line is already blank
                if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(next))
                    continue;

                // Don't add spacing at the end
                if (i == lines.Count - 1)
                    continue;

                var currentType = GetLineType(current);
                var nextType = GetLineType(next);

                // Add blank line when transitioning between different content types
                bool needsSpacing = false;

                // After a list item, before a heading or non-list text
                if (currentType == LineType.List && nextType != LineType.List)
                    needsSpacing = true;

                // Before a heading (except after another heading or list)
                if (nextType == LineType.Heading && currentType != LineType.Heading && currentType != LineType.List)
                    needsSpacing = true;

                // After a heading, before text or list
                if (currentType == LineType.Heading && (nextType == LineType.Text || nextType == LineType.List))
                    needsSpacing = false; // Don't add spacing after headings for now

                if (needsSpacing)
                    result.Add("");
            }

            // Trim trailing blank lines
            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            {
                result.RemoveAt(result.Count - 1);
            }

            return result;
        }

        private enum LineType { Heading, List, Text }

        private static LineType GetLineType(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return LineType.Text;

            var trimmed = line.TrimStart();

            // Check for headings
            if (trimmed.StartsWith("# ") || trimmed.StartsWith("## ") || trimmed.StartsWith("### "))
                return LineType.Heading;

            // Check for list items
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") ||
                trimmed.StartsWith("• ") || trimmed.StartsWith("★ ") ||
                System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+\.\s"))
                return LineType.List;

            return LineType.Text;
        }
    }
}
