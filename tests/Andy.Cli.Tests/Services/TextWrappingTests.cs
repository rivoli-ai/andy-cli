using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Andy.Cli.Services.TextWrapping;

namespace Andy.Cli.Tests.Services.TextWrapping;

/// <summary>
/// Tests for the SimpleTextWrapper implementation.
/// </summary>
public class SimpleTextWrapperTests
{
    private readonly SimpleTextWrapper _wrapper;
    private readonly TextWrappingOptions _defaultOptions;

    public SimpleTextWrapperTests()
    {
        var hyphenationService = new SimpleHyphenationService();
        _wrapper = new SimpleTextWrapper(hyphenationService);
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
        Assert.Equal(1, result.LineCount);
    }

    [Fact]
    public void WrapText_NullText_ReturnsSingleEmptyLine()
    {
        // Act
        var result = _wrapper.WrapText(null!, 80, _defaultOptions);

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
    public void WrapText_LongText_WrapsAtWordBoundaries()
    {
        // Arrange
        var text = "This is a very long line of text that should be wrapped at word boundaries when it exceeds the maximum width";
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
    public void WrapText_PreservesExistingLineBreaks()
    {
        // Arrange
        var text = "First line\nSecond line\nThird line";
        var maxWidth = 50;

        // Act
        var result = _wrapper.WrapText(text, maxWidth, _defaultOptions);

        // Assert
        Assert.Equal(3, result.LineCount);
        Assert.Equal("First line", result.Lines[0]);
        Assert.Equal("Second line", result.Lines[1]);
        Assert.Equal("Third line", result.Lines[2]);
    }

    [Fact]
    public void WrapText_WithHyphenation_EnablesWordBreaking()
    {
        // Arrange
        var text = "supercalifragilisticexpialidocious";
        var maxWidth = 15;
        var options = new TextWrappingOptions 
        { 
            EnableHyphenation = true,
            MinHyphenationLength = 3,
            MaxHyphenationLength = 15 // Allow longer fragments
        };

        // Act
        var result = _wrapper.WrapText(text, maxWidth, options);

        // Assert
        Assert.True(result.LineCount > 1);
        Assert.True(result.HasHyphenation);
    }

    [Fact]
    public void WrapText_WithoutHyphenation_BreaksAtCharacterBoundaries()
    {
        // Arrange
        var text = "supercalifragilisticexpialidocious";
        var maxWidth = 15;
        var options = new TextWrappingOptions { EnableHyphenation = false };

        // Act
        var result = _wrapper.WrapText(text, maxWidth, options);

        // Assert
        Assert.True(result.LineCount > 1);
        Assert.False(result.HasHyphenation);
    }

    [Fact]
    public void WrapText_TrimLines_RemovesTrailingWhitespace()
    {
        // Arrange
        var text = "Line with spaces   \nAnother line   ";
        var options = new TextWrappingOptions { TrimLines = true };

        // Act
        var result = _wrapper.WrapText(text, 50, options);

        // Assert
        foreach (var line in result.Lines)
        {
            Assert.False(line.EndsWith(' '), $"Line '{line}' has trailing whitespace");
        }
    }

    [Fact]
    public void MeasureLineCount_ReturnsCorrectCount()
    {
        // Arrange
        var text = "This is a test line that will be wrapped";
        var maxWidth = 20;

        // Act
        var lineCount = _wrapper.MeasureLineCount(text, maxWidth, _defaultOptions);
        var wrappedText = _wrapper.WrapText(text, maxWidth, _defaultOptions);

        // Assert
        Assert.Equal(wrappedText.LineCount, lineCount);
    }

    [Fact]
    public void WrapText_MultipleParagraphs_HandlesCorrectly()
    {
        // Arrange
        var text = "First paragraph with multiple words.\n\nSecond paragraph with different content.";
        var maxWidth = 25;

        // Act
        var result = _wrapper.WrapText(text, maxWidth, _defaultOptions);

        // Assert
        Assert.True(result.LineCount > 2);
        // Should preserve paragraph breaks
        var hasEmptyLine = result.Lines.Any(line => string.IsNullOrEmpty(line));
        Assert.True(hasEmptyLine);
    }
}

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
    public void WrapText_LongText_OptimizesLineBreaks()
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
    public void WrapText_WithHyphenation_UsesOptimalHyphenation()
    {
        // Arrange
        var text = "supercalifragilisticexpialidocious";
        var maxWidth = 15;
        var options = new TextWrappingOptions 
        { 
            EnableHyphenation = true,
            MinHyphenationLength = 3,
            MaxHyphenationLength = 15 // Allow longer fragments
        };

        // Act
        var result = _wrapper.WrapText(text, maxWidth, options);

        // Assert
        Assert.True(result.LineCount > 1);
        // Knuth-Plass should find optimal hyphenation points
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
    public void GetHyphenationPoints_ShortWord_ReturnsEmpty()
    {
        // Act
        var points = _service.GetHyphenationPoints("cat");

        // Assert
        Assert.Empty(points);
    }

    [Fact]
    public void GetHyphenationPoints_LongWord_ReturnsPoints()
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
        Assert.True(_service.CanHyphenate("computer"));
    }

    [Fact]
    public void LanguageCode_ReturnsEnglish()
    {
        // Act & Assert
        Assert.Equal("en", _service.LanguageCode);
    }
}

/// <summary>
/// Tests for the TeXHyphenationService implementation.
/// </summary>
public class TeXHyphenationServiceTests
{
    private readonly TeXHyphenationService _service;

    public TeXHyphenationServiceTests()
    {
        _service = new TeXHyphenationService();
    }

    [Fact]
    public void GetHyphenationPoints_ShortWord_ReturnsEmpty()
    {
        // Act
        var points = _service.GetHyphenationPoints("cat");

        // Assert
        Assert.Empty(points);
    }

    [Fact]
    public void GetHyphenationPoints_LongWord_ReturnsPoints()
    {
        // Act
        var points = _service.GetHyphenationPoints("beautiful");

        // Assert
        Assert.NotEmpty(points);
        foreach (var point in points)
        {
            Assert.True(point >= 2, "Hyphenation point too close to beginning");
            Assert.True(point <= 6, "Hyphenation point too close to end");
        }
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
        Assert.True(_service.CanHyphenate("beautiful"));
        Assert.True(_service.CanHyphenate("computer"));
        Assert.True(_service.CanHyphenate("development"));
    }

    [Fact]
    public void LanguageCode_ReturnsEnglish()
    {
        // Act & Assert
        Assert.Equal("en", _service.LanguageCode);
    }
}

/// <summary>
/// Tests for the TextWrappingOptions record.
/// </summary>
public class TextWrappingOptionsTests
{
    [Fact]
    public void DefaultOptions_HasCorrectDefaults()
    {
        // Act
        var options = new TextWrappingOptions();

        // Assert
        Assert.True(options.PreferWordBoundaries);
        Assert.True(options.EnableHyphenation);
        Assert.Equal(3, options.MinHyphenationLength);
        Assert.Equal(5, options.MaxHyphenationLength);
        Assert.Equal(TextWrappingMode.Balanced, options.Mode);
        Assert.True(options.PreserveLineBreaks);
        Assert.True(options.TrimLines);
    }

    [Fact]
    public void CustomOptions_CanBeSet()
    {
        // Act
        var options = new TextWrappingOptions
        {
            PreferWordBoundaries = false,
            EnableHyphenation = false,
            MinHyphenationLength = 2,
            MaxHyphenationLength = 8,
            Mode = TextWrappingMode.Optimal,
            PreserveLineBreaks = false,
            TrimLines = false
        };

        // Assert
        Assert.False(options.PreferWordBoundaries);
        Assert.False(options.EnableHyphenation);
        Assert.Equal(2, options.MinHyphenationLength);
        Assert.Equal(8, options.MaxHyphenationLength);
        Assert.Equal(TextWrappingMode.Optimal, options.Mode);
        Assert.False(options.PreserveLineBreaks);
        Assert.False(options.TrimLines);
    }
}

/// <summary>
/// Tests for the WrappedText class.
/// </summary>
public class WrappedTextTests
{
    [Fact]
    public void Constructor_WithLines_SetsProperties()
    {
        // Arrange
        var lines = new[] { "Line 1", "Line 2", "Line 3" };

        // Act
        var wrappedText = new WrappedText(lines);

        // Assert
        Assert.Equal(3, wrappedText.LineCount);
        Assert.Equal(lines, wrappedText.Lines);
        Assert.False(wrappedText.HasHyphenation);
        Assert.Equal(6, wrappedText.MaxLineWidth); // "Line 1" is 6 characters
    }

    [Fact]
    public void Constructor_WithHyphenation_SetsHasHyphenation()
    {
        // Arrange
        var lines = new[] { "Line 1", "Line 2-" };

        // Act
        var wrappedText = new WrappedText(lines, hasHyphenation: true);

        // Assert
        Assert.True(wrappedText.HasHyphenation);
    }

    [Fact]
    public void ToString_ReturnsJoinedLines()
    {
        // Arrange
        var lines = new[] { "Line 1", "Line 2", "Line 3" };
        var wrappedText = new WrappedText(lines);

        // Act
        var result = wrappedText.ToString();

        // Assert
        Assert.Equal("Line 1\nLine 2\nLine 3", result);
    }

    [Fact]
    public void MaxLineWidth_CalculatesCorrectly()
    {
        // Arrange
        var lines = new[] { "Short", "This is a longer line", "Medium" };
        var wrappedText = new WrappedText(lines);

        // Assert
        Assert.Equal(21, wrappedText.MaxLineWidth); // "This is a longer line" is 21 characters
    }
}

/// <summary>
/// Integration tests for text wrapping with real-world content.
/// </summary>
public class TextWrappingIntegrationTests
{
    private readonly SimpleTextWrapper _simpleWrapper;
    private readonly KnuthPlassTextWrapper _knuthPlassWrapper;
    private readonly TextWrappingOptions _defaultOptions;

    public TextWrappingIntegrationTests()
    {
        var hyphenationService = new SimpleHyphenationService();
        _simpleWrapper = new SimpleTextWrapper(hyphenationService);
        _knuthPlassWrapper = new KnuthPlassTextWrapper(hyphenationService);
        _defaultOptions = new TextWrappingOptions();
    }

    [Fact]
    public void WrapText_ArthurDentStory_WellWrappedWithSimpleWrapper()
    {
        // Arrange
        var arthurDentText = "Arthur Dent's ordinary life is upended when he discovers his house is to be demolished. His friend Ford Prefect, who " +
                           "turns out to be an alien, informs him that Earth is also about to be destroyed by a Vogon constructor fleet to " +
                           "make way for a hyperspace bypass. Ford and Arthur hitch a ride on the Vogon ship, narrowly escaping Earth's destruction. " +
                           "Onboard, they endure Vogon poetry, known as the third worst in the universe, before being ejected into space.";
        
        var maxWidth = 80;

        // Act
        var result = _simpleWrapper.WrapText(arthurDentText, maxWidth, _defaultOptions);

        // Assert
        Assert.True(result.LineCount > 1, "Text should be wrapped into multiple lines");
        
        // Verify no line exceeds the maximum width
        foreach (var line in result.Lines)
        {
            Assert.True(line.Length <= maxWidth, $"Line '{line}' exceeds max width {maxWidth} (length: {line.Length})");
        }

        // Verify text content is preserved (no words lost)
        var originalWords = arthurDentText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wrappedText = string.Join(" ", result.Lines);
        var wrappedWords = wrappedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        Assert.Equal(originalWords.Length, wrappedWords.Length);
        
        // Verify key words are present
        Assert.Contains("Arthur", wrappedText);
        Assert.Contains("Ford", wrappedText);
        Assert.Contains("Vogon", wrappedText);
        Assert.Contains("Earth", wrappedText);
        Assert.Contains("hyperspace", wrappedText);
    }

    [Fact]
    public void WrapText_ArthurDentStory_WellWrappedWithKnuthPlassWrapper()
    {
        // Arrange
        var arthurDentText = "Arthur Dent's ordinary life is upended when he discovers his house is to be demolished. His friend Ford Prefect, who " +
                           "turns out to be an alien, informs him that Earth is also about to be destroyed by a Vogon constructor fleet to " +
                           "make way for a hyperspace bypass. Ford and Arthur hitch a ride on the Vogon ship, narrowly escaping Earth's destruction. " +
                           "Onboard, they endure Vogon poetry, known as the third worst in the universe, before being ejected into space.";
        
        var maxWidth = 80;

        // Act
        var result = _knuthPlassWrapper.WrapText(arthurDentText, maxWidth, _defaultOptions);

        // Assert
        Assert.True(result.LineCount > 1, "Text should be wrapped into multiple lines");
        
        // Verify no line exceeds the maximum width
        foreach (var line in result.Lines)
        {
            Assert.True(line.Length <= maxWidth, $"Line '{line}' exceeds max width {maxWidth} (length: {line.Length})");
        }

        // Verify text content is preserved
        var originalWords = arthurDentText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wrappedText = string.Join(" ", result.Lines);
        var wrappedWords = wrappedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        Assert.Equal(originalWords.Length, wrappedWords.Length);
        
        // Verify key words are present
        Assert.Contains("Arthur", wrappedText);
        Assert.Contains("Ford", wrappedText);
        Assert.Contains("Vogon", wrappedText);
        Assert.Contains("Earth", wrappedText);
        Assert.Contains("hyperspace", wrappedText);
    }

    [Fact]
    public void WrapText_ArthurDentStory_DifferentWidths_AllWellWrapped()
    {
        // Arrange
        var arthurDentText = "Arthur Dent's ordinary life is upended when he discovers his house is to be demolished. His friend Ford Prefect, who " +
                           "turns out to be an alien, informs him that Earth is also about to be destroyed by a Vogon constructor fleet to " +
                           "make way for a hyperspace bypass. Ford and Arthur hitch a ride on the Vogon ship, narrowly escaping Earth's destruction. " +
                           "Onboard, they endure Vogon poetry, known as the third worst in the universe, before being ejected into space.";
        
        var widths = new[] { 40, 60, 80, 100, 120 };

        foreach (var maxWidth in widths)
        {
            // Act
            var simpleResult = _simpleWrapper.WrapText(arthurDentText, maxWidth, _defaultOptions);
            var knuthPlassResult = _knuthPlassWrapper.WrapText(arthurDentText, maxWidth, _defaultOptions);

            // Assert - Simple wrapper
            foreach (var line in simpleResult.Lines)
            {
                Assert.True(line.Length <= maxWidth, $"Simple wrapper: Line '{line}' exceeds max width {maxWidth} (length: {line.Length})");
            }

            // Assert - Knuth-Plass wrapper
            foreach (var line in knuthPlassResult.Lines)
            {
                Assert.True(line.Length <= maxWidth, $"Knuth-Plass wrapper: Line '{line}' exceeds max width {maxWidth} (length: {line.Length})");
            }

            // Both wrappers should preserve content
            var simpleText = string.Join(" ", simpleResult.Lines);
            var knuthPlassText = string.Join(" ", knuthPlassResult.Lines);
            
            Assert.Contains("Arthur", simpleText);
            Assert.Contains("Arthur", knuthPlassText);
            Assert.Contains("Vogon", simpleText);
            Assert.Contains("Vogon", knuthPlassText);
        }
    }

    [Fact]
    public void WrapText_ArthurDentStory_WithHyphenation_HandlesLongWords()
    {
        // Arrange
        var arthurDentText = "Arthur Dent's ordinary life is upended when he discovers his house is to be demolished. His friend Ford Prefect, who " +
                           "turns out to be an alien, informs him that Earth is also about to be destroyed by a Vogon constructor fleet to " +
                           "make way for a hyperspace bypass. Ford and Arthur hitch a ride on the Vogon ship, narrowly escaping Earth's destruction. " +
                           "Onboard, they endure Vogon poetry, known as the third worst in the universe, before being ejected into space.";
        
        var maxWidth = 30; // Narrow width to force hyphenation
        var options = new TextWrappingOptions { EnableHyphenation = true };

        // Act
        var result = _simpleWrapper.WrapText(arthurDentText, maxWidth, options);

        // Assert
        Assert.True(result.LineCount > 1, "Text should be wrapped into multiple lines");
        
        // Verify no line exceeds the maximum width
        foreach (var line in result.Lines)
        {
            Assert.True(line.Length <= maxWidth, $"Line '{line}' exceeds max width {maxWidth} (length: {line.Length})");
        }

        // Verify content is preserved
        var wrappedText = string.Join(" ", result.Lines);
        Assert.Contains("Arthur", wrappedText);
        Assert.Contains("Ford", wrappedText);
        Assert.Contains("Vogon", wrappedText);
    }

    [Fact]
    public void WrapText_ArthurDentStory_WithoutHyphenation_StillWrapsCorrectly()
    {
        // Arrange
        var arthurDentText = "Arthur Dent's ordinary life is upended when he discovers his house is to be demolished. His friend Ford Prefect, who " +
                           "turns out to be an alien, informs him that Earth is also about to be destroyed by a Vogon constructor fleet to " +
                           "make way for a hyperspace bypass. Ford and Arthur hitch a ride on the Vogon ship, narrowly escaping Earth's destruction. " +
                           "Onboard, they endure Vogon poetry, known as the third worst in the universe, before being ejected into space.";
        
        var maxWidth = 30;
        var options = new TextWrappingOptions { EnableHyphenation = false };

        // Act
        var result = _simpleWrapper.WrapText(arthurDentText, maxWidth, options);

        // Assert
        Assert.True(result.LineCount > 1, "Text should be wrapped into multiple lines");
        
        // Verify no line exceeds the maximum width
        foreach (var line in result.Lines)
        {
            Assert.True(line.Length <= maxWidth, $"Line '{line}' exceeds max width {maxWidth} (length: {line.Length})");
        }

        // Verify content is preserved
        var wrappedText = string.Join(" ", result.Lines);
        Assert.Contains("Arthur", wrappedText);
        Assert.Contains("Ford", wrappedText);
        Assert.Contains("Vogon", wrappedText);
        
        // Should not have hyphenation
        Assert.False(result.HasHyphenation);
    }
}

/// <summary>
/// Tests for TextWrappingOptions extension methods.
/// </summary>
public class TextWrappingOptionsExtensionsTests
{
    [Fact]
    public void WithHyphenation_EnablesHyphenation()
    {
        // Arrange
        var options = new TextWrappingOptions { EnableHyphenation = false };

        // Act
        var result = options.WithHyphenation();

        // Assert
        Assert.True(result.EnableHyphenation);
        Assert.False(options.EnableHyphenation); // Original should be unchanged
    }

    [Fact]
    public void WithoutHyphenation_DisablesHyphenation()
    {
        // Arrange
        var options = new TextWrappingOptions { EnableHyphenation = true };

        // Act
        var result = options.WithoutHyphenation();

        // Assert
        Assert.False(result.EnableHyphenation);
        Assert.True(options.EnableHyphenation); // Original should be unchanged
    }

    [Fact]
    public void WithHyphenationLengths_SetsLengthConstraints()
    {
        // Arrange
        var options = new TextWrappingOptions();

        // Act
        var result = options.WithHyphenationLengths(2, 8);

        // Assert
        Assert.Equal(2, result.MinHyphenationLength);
        Assert.Equal(8, result.MaxHyphenationLength);
        Assert.Equal(3, options.MinHyphenationLength); // Original should be unchanged
        Assert.Equal(5, options.MaxHyphenationLength); // Original should be unchanged
    }

    [Fact]
    public void WithMode_SetsWrappingMode()
    {
        // Arrange
        var options = new TextWrappingOptions();

        // Act
        var result = options.WithMode(TextWrappingMode.Optimal);

        // Assert
        Assert.Equal(TextWrappingMode.Optimal, result.Mode);
        Assert.Equal(TextWrappingMode.Balanced, options.Mode); // Original should be unchanged
    }

    [Fact]
    public void WithHyphenation_WorksWithTextWrapper()
    {
        // Arrange
        var hyphenationService = new SimpleHyphenationService();
        var wrapper = new SimpleTextWrapper(hyphenationService);
        var text = "supercalifragilisticexpialidocious";
        var maxWidth = 15;

        // Act
        var result = wrapper.WrapText(text, maxWidth, new TextWrappingOptions()
            .WithHyphenation()
            .WithHyphenationLengths(3, 15)); // Allow longer fragments

        // Assert
        Assert.True(result.LineCount > 1);
        Assert.True(result.HasHyphenation);
    }

    [Fact]
    public void FluentApi_CanChainMultipleMethods()
    {
        // Arrange
        var options = new TextWrappingOptions();

        // Act
        var result = options
            .WithHyphenation()
            .WithMode(TextWrappingMode.Optimal)
            .WithHyphenationLengths(2, 8)
            .WithTrimming();

        // Assert
        Assert.True(result.EnableHyphenation);
        Assert.Equal(TextWrappingMode.Optimal, result.Mode);
        Assert.Equal(2, result.MinHyphenationLength);
        Assert.Equal(8, result.MaxHyphenationLength);
        Assert.True(result.TrimLines);
    }
}