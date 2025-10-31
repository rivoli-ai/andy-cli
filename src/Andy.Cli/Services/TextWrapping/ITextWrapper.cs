namespace Andy.Cli.Services.TextWrapping;

/// <summary>
/// Interface for text wrapping services that can break text into lines respecting word boundaries and hyphenation.
/// </summary>
public interface ITextWrapper
{
    /// <summary>
    /// Wraps text to fit within the specified maximum width.
    /// </summary>
    /// <param name="text">The text to wrap</param>
    /// <param name="maxWidth">Maximum width in characters</param>
    /// <param name="options">Wrapping options</param>
    /// <returns>Wrapped text with line breaks</returns>
    WrappedText WrapText(string text, int maxWidth, TextWrappingOptions? options = default);

    /// <summary>
    /// Measures how many lines the text will occupy when wrapped.
    /// </summary>
    /// <param name="text">The text to measure</param>
    /// <param name="maxWidth">Maximum width in characters</param>
    /// <param name="options">Wrapping options</param>
    /// <returns>Number of lines needed</returns>
    int MeasureLineCount(string text, int maxWidth, TextWrappingOptions? options = default);
}

