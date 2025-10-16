using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

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

        /// <summary>
        /// Helper method that simulates the AddParagraphSpacing logic
        /// This allows testing the spacing logic without needing full rendering
        /// </summary>
        private static string[] ProcessMarkdownSpacing(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
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
