using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Cli.Services.ContentPipeline;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Integration tests specifically focused on the gap removal issue
/// </summary>
public class IntegrationGapTest
{
    // Regression guard for the user-reported bug where a long run of blank lines caused the text
    // that follows it ("Now that we have the initial results...") to be dropped from the rendered
    // output. That content loss is fixed: the pipeline now preserves every paragraph and collapses a
    // runaway blank-line run to a single blank line (paragraph spacing), rather than removing the gap
    // entirely or losing what comes after it.
    [Fact]
    public async Task Pipeline_Should_Remove_User_Reported_Gaps()
    {
        // Arrange - Direct test of the exact user-reported content with gaps
        var userReportedContent = @"Based on these results, we can start migrating the project to the newer .NET
version while ensuring compatibility and maintaining the project's integrity.
Please output the results of the next query:















Now that we have the initial results from code_index and list_directory,
let's use them to better understand the project structure and plan the migration process.
1. From code_index, there might be some .NET-related symbols like namespaces, classes, and methods found in the project. This suggests that the project has some .NET components.";

        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var captureRenderer = new CapturingRenderer();
        var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, captureRenderer);

        // Act
        pipeline.AddRawContent(userReportedContent);
        await pipeline.FinalizeAsync();

        // Assert
        var renderedOutput = captureRenderer.GetAllContent();

        // No content may be lost after the long blank-line run (the original bug).
        Assert.Contains("Based on these results", renderedOutput);
        Assert.Contains("Please output the results", renderedOutput);
        Assert.Contains("Now that we have the initial results", renderedOutput);
        Assert.Contains("plan the migration process", renderedOutput);
        Assert.Contains(".NET-related symbols", renderedOutput);

        // The runaway blank-line run collapses to exactly one blank line (paragraph spacing
        // preserved), and nothing following it is dropped.
        Assert.Contains("next query:\n\nNow that we have the initial results", renderedOutput);

        // No runaway gaps remain anywhere (3+ newline runs are collapsed).
        Assert.DoesNotContain("\n\n\n", renderedOutput);

        // Full golden output: single newlines within paragraphs, exactly one blank line at the gap.
        var expected = @"Based on these results, we can start migrating the project to the newer .NET
version while ensuring compatibility and maintaining the project's integrity.
Please output the results of the next query:

Now that we have the initial results from code_index and list_directory,
let's use them to better understand the project structure and plan the migration process.
1. From code_index, there might be some .NET-related symbols like namespaces, classes, and methods found in the project. This suggests that the project has some .NET components.";

        Assert.Equal(expected, renderedOutput);

        pipeline.Dispose();
    }

    [Fact]
    public async Task Pipeline_Should_Handle_Mixed_Content_Without_Gaps()
    {
        // Arrange - Test with code blocks and text with gaps
        var mixedContent = @"Here's the analysis:





```csharp
public void TestMethod()
{
    Console.WriteLine(""Hello"");
}
```





The code above shows a simple test.





More analysis here.";

        var processor = new MarkdownContentProcessor();
        var sanitizer = new TextContentSanitizer();
        var captureRenderer = new CapturingRenderer();
        var pipeline = new Andy.Cli.Services.ContentPipeline.ContentPipeline(processor, sanitizer, captureRenderer);

        // Act
        pipeline.AddRawContent(mixedContent);
        await pipeline.FinalizeAsync();

        // Assert
        var renderedOutput = captureRenderer.GetAllContent();

        // Should not contain excessive blank runs. A single blank line between
        // paragraphs (\n\n) is legitimate spacing that the pipeline preserves;
        // only runaway 3+ newline gaps are collapsed, so assert no triple newline.
        Assert.DoesNotContain("\n\n\n", renderedOutput);

        // Should contain all content sections
        Assert.Contains("Here's the analysis:", renderedOutput);
        Assert.Contains("TestMethod", renderedOutput);
        Assert.Contains("The code above shows", renderedOutput);
        Assert.Contains("More analysis here", renderedOutput);

        pipeline.Dispose();
    }
}

/// <summary>
/// Test renderer that captures all rendered content for verification
/// </summary>
public class CapturingRenderer : IContentRenderer
{
    private readonly List<string> _capturedContent = new();

    public void Render(IContentBlock block)
    {
        switch (block)
        {
            case TextBlock textBlock:
                _capturedContent.Add(textBlock.Content);
                break;
            case CodeBlock codeBlock:
                _capturedContent.Add($"[CODE:{codeBlock.Language}]{codeBlock.Code}[/CODE]");
                break;
            case SystemMessageBlock systemBlock:
                _capturedContent.Add($"[SYSTEM:{systemBlock.Type}]{systemBlock.Message}[/SYSTEM]");
                break;
        }
    }

    public string GetAllContent()
    {
        return string.Join("\n", _capturedContent);
    }
}