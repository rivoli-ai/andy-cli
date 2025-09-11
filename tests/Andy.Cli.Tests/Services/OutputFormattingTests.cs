using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class OutputFormattingTests
{
    private readonly Mock<LlmClient> _mockLlmClient;
    private readonly Mock<IToolRegistry> _mockToolRegistry;
    private readonly Mock<IToolExecutor> _mockToolExecutor;
    private readonly FeedView _feedView;
    private readonly Mock<IJsonRepairService> _mockJsonRepair;
    private readonly AiConversationService _service;
    private readonly List<string> _capturedOutput;

    public OutputFormattingTests()
    {
        _mockLlmClient = new Mock<LlmClient>("test-api-key");
        _mockToolRegistry = new Mock<IToolRegistry>();
        _mockToolExecutor = new Mock<IToolExecutor>();
        _feedView = new FeedView();
        _mockJsonRepair = new Mock<IJsonRepairService>();
        _capturedOutput = new List<string>();

        _service = new AiConversationService(
            _mockLlmClient.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _feedView,
            "Test system prompt",
            _mockJsonRepair.Object,
            logger: null,
            modelName: "test-model",
            providerName: "test-provider"
        );
    }

    [Fact]
    public async Task Should_Not_Add_Consecutive_Empty_Lines_To_Output()
    {
        // Arrange
        var testOutput = @"This is line 1.



This is line 2 after multiple empty lines.


This is line 3.";

        // Act
        var sanitized = await ProcessContentThroughPipeline(testOutput);
        var lines = sanitized.Split('\n');

        // Assert
        // With aggressive formatting, there should be no empty lines at all
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                Assert.True(false, $"Found empty line at position {i}");
            }
        }
    }

    [Fact]
    public async Task Should_Not_Output_Empty_Json_Tags()
    {
        // Arrange
        var testOutputs = new[]
        {
            "`json\n\n`",
            "`json\n`",
            "```json\n\n```",
            "```json\n```"
        };

        foreach (var testOutput in testOutputs)
        {
            // Act
            var sanitized = await ProcessContentThroughPipeline(testOutput);
            
            // Debug output
            Console.WriteLine($"Input: {testOutput.Replace("\n", "\\n")}");
            Console.WriteLine($"Output: {sanitized.Replace("\n", "\\n")}");

            // Assert - should be empty or not contain json tags
            Assert.True(string.IsNullOrWhiteSpace(sanitized) || 
                       (!sanitized.Contains("`json") && !sanitized.Contains("```json")),
                       $"Failed to sanitize: Input={testOutput.Replace("\n", "\\n")}, Output={sanitized.Replace("\n", "\\n")}");
        }
    }

    [Fact]
    public async Task Should_Filter_Malformed_Json_Tool_Calls()
    {
        // Arrange
        var testOutput = "Here is the analysis:\n\n" +
            "`json\n" +
            "{\"tool\":\"format_text\",\"parameters\":{\"input_text\":\"<response from json_processor>\",\"operation\":\"sort_lines\",\"options\":{\"sort_order\":\"ascending\"}}}\n" +
            "`\n\n" +
            "The results show that...\n\n" +
            "`json\n\n`\n\n" +
            "Next steps are...";

        // Act
        var sanitized = await ProcessContentThroughPipeline(testOutput);

        // Assert
        // Should not contain empty json blocks
        Assert.DoesNotContain("`json\n\n`", sanitized);
        Assert.DoesNotContain("`json\n`", sanitized);
        
        // Should preserve valid content
        Assert.Contains("Here is the analysis:", sanitized);
        Assert.Contains("The results show that...", sanitized);
        Assert.Contains("Next steps are...", sanitized);
    }

    [Fact]
    public async Task Should_Remove_Excessive_Whitespace_Between_Sections()
    {
        // Arrange
        var testOutput = @"Section 1 content.




Section 2 content.





Section 3 content.";

        // Act
        var sanitized = await ProcessContentThroughPipeline(testOutput);
        
        // Assert
        // Should not have ANY double newlines (everything single-spaced)
        Assert.DoesNotContain("\n\n", sanitized);
        
        // Should preserve section content
        Assert.Contains("Section 1 content.", sanitized);
        Assert.Contains("Section 2 content.", sanitized);
        Assert.Contains("Section 3 content.", sanitized);
    }

    [Fact]
    public async Task Should_Clean_Up_Migration_Output_Example()
    {
        // Arrange
        var testOutput = "Test the project after updating the NuGet packages. The project should now be\n" +
            "migrated to .NET 10, and all dependencies should be compatible.\n\n" +
            "Conclude:\n" +
            "The project has been migrated to .NET 10, and all dependencies have been upda\n" +
            "ted to their .NET 10 compatible versions.\n\n" +
            "Next Steps: Test the project thoroughly to ensure all features and functional\n" +
            "ity work as expected in .NET 10.\n\n\n\n\n\n\n" +
            "`json\n\n`\n" +
            "`json\n\n`\n" +
            "`json\n\n`\n" +
            "`json\n" +
            "{\"tool\":\"format_text\",\"parameters\":{\"input_text\":\"<response from json_processor>\",\"operation\":\"sort_lines\",\"options\":{\"sort_order\":\"ascending\"}}}\n" +
            "`\n" +
            "`json\n\n`\n" +
            "`json\n\n`\n" +
            "This tool will fetch the information from the provided URL, which is the down\n" +
            "load page of the .NET framework.";

        // Act
        var sanitized = await ProcessContentThroughPipeline(testOutput);

        // Assert
        // Should not have empty json blocks
        Assert.DoesNotContain("`json\n\n`", sanitized);
        Assert.DoesNotContain("`json\n`", sanitized);
        
        // Should not have ANY double newlines (everything single-spaced)
        Assert.DoesNotContain("\n\n", sanitized);
        
        // Should preserve meaningful content
        Assert.Contains("Test the project", sanitized);
        Assert.Contains("migrated to .NET 10", sanitized);
        Assert.Contains("Next Steps:", sanitized);
        
        // Should preserve the valid JSON tool call
        Assert.Contains("format_text", sanitized);
        
        // Should preserve the final sentence
        Assert.Contains("This tool will fetch", sanitized);
    }

    // Helper method to test content through pipeline processing
    private async Task<string> ProcessContentThroughPipeline(string text)
    {
        var processor = new Andy.Cli.Services.ContentPipeline.MarkdownContentProcessor();
        var sanitizer = new Andy.Cli.Services.ContentPipeline.TextContentSanitizer();
        var captureRenderer = new CapturingRenderer();
        
        using var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, captureRenderer);
        
        pipeline.AddRawContent(text);
        await pipeline.FinalizeAsync();
        
        return captureRenderer.GetAllContent();
    }
}