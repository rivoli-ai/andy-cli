using Andy.Cli.Services.TextWrapping;

namespace Andy.Cli.Tests.Services.TextWrapping;

/// <summary>
/// Tests for the KnuthPlassTextWrapper implementation.
/// </summary>
public class KnuthPlassTextWrapperTests
{
    private readonly KnuthPlassTextWrapper _wrapper;
    private readonly TextWrappingOptions _defaultOptions;

    public KnuthPlassTextWrapperTests()
    {
        var hyphenationService = new SimpleHyphenationService();
        _wrapper = new KnuthPlassTextWrapper(hyphenationService);
        _defaultOptions = new TextWrappingOptions();
    }

    [Fact]
    public void WrapText_EmptyText_ReturnsSingleEmptyLine()
    {
        // Act
        var result = _wrapper.WrapText("", 80, _defaultOptions);

        // Assert
        Assert.Single(result.Lines);
        Assert.Equal("", result.Lines[0]);
    }

    [Fact]
    public void WrapText_ShortText_ReturnsSingleLine()
    {
        // Arrange
        var text = "Hello world";

        // Act
        var result = _wrapper.WrapText(text, 80, _defaultOptions);

        // Assert
        Assert.Single(result.Lines);
        Assert.Equal("Hello world", result.Lines[0]);
    }

    [Fact]
    public void WrapText_LongText_WrapsCorrectly()
    {
        // Arrange
        var text = "This is a very long line of text that should be wrapped optimally using the Knuth-Plass algorithm";
        var maxWidth = 30;

        // Act
        var result = _wrapper.WrapText(text, maxWidth, _defaultOptions);

        // Assert
        Assert.True(result.LineCount > 1);
        foreach (var line in result.Lines)
        {
            Assert.True(line.Length <= maxWidth, $"Line '{line}' exceeds max width {maxWidth}");
        }
    }

    [Fact]
    public void WrapText_WithHyphenation_UsesHyphenation()
    {
        // Arrange
        var text = "supercalifragilisticexpialidocious";
        var maxWidth = 15;
        var options = new TextWrappingOptions 
        { 
            EnableHyphenation = true,
            MinHyphenationLength = 3,
            MaxHyphenationLength = 15
        };

        // Act
        var result = _wrapper.WrapText(text, maxWidth, options);

        // Assert
        Assert.True(result.LineCount > 1);
        Assert.True(result.HasHyphenation);
    }

    [Fact]
    public void MeasureLineCount_ReturnsCorrectCount()
    {
        // Arrange
        var text = "This is a test line that will be wrapped optimally";
        var maxWidth = 20;

        // Act
        var lineCount = _wrapper.MeasureLineCount(text, maxWidth, _defaultOptions);
        var wrappedText = _wrapper.WrapText(text, maxWidth, _defaultOptions);

        // Assert
        Assert.Equal(wrappedText.LineCount, lineCount);
    }
}

/// <summary>
/// Tests for the SimpleHyphenationService implementation.
/// </summary>
public class SimpleHyphenationServiceTests
{
    private readonly SimpleHyphenationService _service;

    public SimpleHyphenationServiceTests()
    {
        _service = new SimpleHyphenationService();
    }

    [Fact]
    public void CanHyphenate_ShortWord_ReturnsFalse()
    {
        // Act & Assert
        Assert.False(_service.CanHyphenate("cat"));
        Assert.False(_service.CanHyphenate(""));
        Assert.False(_service.CanHyphenate(null!));
    }

    [Fact]
    public void CanHyphenate_LongWord_ReturnsTrue()
    {
        // Act & Assert
        Assert.True(_service.CanHyphenate("supercalifragilisticexpialidocious"));
        Assert.True(_service.CanHyphenate("beautiful"));
    }

    [Fact]
    public void GetHyphenationPoints_LongWord_ReturnsValidPoints()
    {
        // Act
        var points = _service.GetHyphenationPoints("supercalifragilisticexpialidocious");

        // Assert
        Assert.NotEmpty(points);
        foreach (var point in points)
        {
            Assert.True(point >= 2, "Hyphenation point too close to beginning");
            Assert.True(point <= 30, "Hyphenation point too close to end");
        }
    }
}

/// <summary>
/// Integration test for text wrapping with real-world content.
/// </summary>
public class TextWrappingIntegrationTests
{
    [Fact]
    public void WrapText_RealWorldContent_WrapsCorrectly()
    {
        // Arrange
        var hyphenationService = new SimpleHyphenationService();
        var wrapper = new KnuthPlassTextWrapper(hyphenationService);
        var text = "Arthur Dent's ordinary life is upended when he discovers his house is to be demolished. His friend Ford Prefect, who " +
                   "turns out to be an alien, informs him that Earth is also about to be destroyed by a Vogon constructor fleet to " +
                   "make way for a hyperspace bypass.";
        var maxWidth = 80;
        var options = new TextWrappingOptions().WithHyphenation().WithHyphenationLengths(3, 15);

        // Act
        var result = wrapper.WrapText(text, maxWidth, options);

        // Assert
        Assert.True(result.LineCount > 1);
        foreach (var line in result.Lines)
        {
            Assert.True(line.Length <= maxWidth, $"Line '{line}' exceeds max width {maxWidth}");
        }
        var wrappedText = string.Join(" ", result.Lines);
        Assert.Contains("Arthur", wrappedText);
        Assert.Contains("Ford", wrappedText);
        Assert.Contains("Vogon", wrappedText);
    }
}

/// <summary>
/// Tests for TextWrappingOptions extension methods (fluent API).
/// </summary>
public class TextWrappingOptionsExtensionsTests
{
    [Fact]
    public void FluentApi_WorksWithTextWrapper()
    {
        // Arrange
        var hyphenationService = new SimpleHyphenationService();
        var wrapper = new KnuthPlassTextWrapper(hyphenationService);
        var text = "supercalifragilisticexpialidocious";
        var maxWidth = 15;

        // Act
        var result = wrapper.WrapText(text, maxWidth, new TextWrappingOptions()
            .WithHyphenation()
            .WithHyphenationLengths(3, 15));

        // Assert
        Assert.True(result.LineCount > 1);
        Assert.True(result.HasHyphenation);
    }
}
