using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Tests for the de-duplication guard that decides whether an intermediate assistant narration
/// (text the model emits alongside tool calls while a turn is in progress) should be rendered to
/// the feed.
/// </summary>
public class IntermediateAssistantTextTests
{
    [Fact]
    public void FirstNonEmptyNarration_IsRendered()
    {
        Assert.True(SimpleAssistantService.ShouldRenderIntermediateText(
            "I'll first read the file...", previous: null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void EmptyOrWhitespaceNarration_IsNotRendered(string? text)
    {
        Assert.False(SimpleAssistantService.ShouldRenderIntermediateText(text, previous: null));
    }

    [Fact]
    public void NarrationIdenticalToPrevious_IsNotRendered()
    {
        Assert.False(SimpleAssistantService.ShouldRenderIntermediateText(
            "Reading the config now.", previous: "Reading the config now."));
    }

    [Fact]
    public void NarrationDifferingOnlyByWhitespace_IsTreatedAsDuplicate()
    {
        // Trimmed comparison: leading/trailing whitespace differences are still duplicates.
        Assert.False(SimpleAssistantService.ShouldRenderIntermediateText(
            "  Reading the config now.  ", previous: "Reading the config now."));
    }

    [Fact]
    public void DifferentNarration_IsRendered()
    {
        Assert.True(SimpleAssistantService.ShouldRenderIntermediateText(
            "Now I'll edit the file.", previous: "I'll first read the file..."));
    }
}
