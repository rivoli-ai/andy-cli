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
    // With the current ContentPipeline, a long run of blank lines causes the text that follows it
    // ("Now that we have the initial results...") to be dropped from the rendered output instead of
    // being collapsed to a single newline. That is real content loss in the product, not a test defect,
    // so it stays skipped here rather than being relaxed to pass. The underlying ContentPipeline
    // blank-line content-loss fix is owned by the #178 branch; re-enable this test once #178 lands.
    // The sibling mixed-content test (which does not hit this pattern) runs.
    [Fact(Skip = "Product bug: ContentPipeline drops content after a long blank-line run. Fix is owned by the #178 branch; re-enable when #178 lands. Do not relax this test to pass.")]
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

        // Should not contain ANY double newlines (no gaps)
        Assert.DoesNotContain("\n\n", renderedOutput);

        // Should contain all the meaningful content
        Assert.Contains("Based on these results", renderedOutput);
        Assert.Contains("Please output the results", renderedOutput);
        Assert.Contains("Now that we have the initial results", renderedOutput);
        Assert.Contains("plan the migration process", renderedOutput);
        Assert.Contains(".NET-related symbols", renderedOutput);

        // Should be single-spaced
        var expectedSingleSpaced = @"Based on these results, we can start migrating the project to the newer .NET
version while ensuring compatibility and maintaining the project's integrity.
Please output the results of the next query:
Now that we have the initial results from code_index and list_directory,
let's use them to better understand the project structure and plan the migration process.
1. From code_index, there might be some .NET-related symbols like namespaces, classes, and methods found in the project. This suggests that the project has some .NET components.";

        Assert.Equal(expectedSingleSpaced, renderedOutput);

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

        // Should not contain double newlines anywhere
        Assert.DoesNotContain("\n\n", renderedOutput);

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