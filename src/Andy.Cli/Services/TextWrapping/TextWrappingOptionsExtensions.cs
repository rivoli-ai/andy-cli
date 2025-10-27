using System;
using System.Collections.Generic;

namespace Andy.Cli.Services.TextWrapping;

/// <summary>
/// Extension methods for TextWrappingOptions to provide fluent API.
/// </summary>
public static class TextWrappingOptionsExtensions
{
    /// <summary>
    /// Enables hyphenation for text wrapping.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <returns>New options with hyphenation enabled</returns>
    public static TextWrappingOptions WithHyphenation(this TextWrappingOptions options)
    {
        return options with { EnableHyphenation = true };
    }

    /// <summary>
    /// Disables hyphenation for text wrapping.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <returns>New options with hyphenation disabled</returns>
    public static TextWrappingOptions WithoutHyphenation(this TextWrappingOptions options)
    {
        return options with { EnableHyphenation = false };
    }

    /// <summary>
    /// Sets the hyphenation length constraints.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <param name="minLength">Minimum length of word fragment when hyphenating</param>
    /// <param name="maxLength">Maximum length of word fragment when hyphenating</param>
    /// <returns>New options with updated hyphenation length constraints</returns>
    public static TextWrappingOptions WithHyphenationLengths(this TextWrappingOptions options, int minLength, int maxLength)
    {
        return options with 
        { 
            MinHyphenationLength = minLength, 
            MaxHyphenationLength = maxLength 
        };
    }

    /// <summary>
    /// Sets the text wrapping mode.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <param name="mode">The wrapping algorithm mode to use</param>
    /// <returns>New options with updated wrapping mode</returns>
    public static TextWrappingOptions WithMode(this TextWrappingOptions options, TextWrappingMode mode)
    {
        return options with { Mode = mode };
    }

    /// <summary>
    /// Enables word boundary preference.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <returns>New options with word boundary preference enabled</returns>
    public static TextWrappingOptions WithWordBoundaries(this TextWrappingOptions options)
    {
        return options with { PreferWordBoundaries = true };
    }

    /// <summary>
    /// Disables word boundary preference.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <returns>New options with word boundary preference disabled</returns>
    public static TextWrappingOptions WithoutWordBoundaries(this TextWrappingOptions options)
    {
        return options with { PreferWordBoundaries = false };
    }

    /// <summary>
    /// Enables line trimming.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <returns>New options with line trimming enabled</returns>
    public static TextWrappingOptions WithTrimming(this TextWrappingOptions options)
    {
        return options with { TrimLines = true };
    }

    /// <summary>
    /// Disables line trimming.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <returns>New options with line trimming disabled</returns>
    public static TextWrappingOptions WithoutTrimming(this TextWrappingOptions options)
    {
        return options with { TrimLines = false };
    }

    /// <summary>
    /// Enables preservation of existing line breaks.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <returns>New options with line break preservation enabled</returns>
    public static TextWrappingOptions WithLineBreakPreservation(this TextWrappingOptions options)
    {
        return options with { PreserveLineBreaks = true };
    }

    /// <summary>
    /// Disables preservation of existing line breaks.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <returns>New options with line break preservation disabled</returns>
    public static TextWrappingOptions WithoutLineBreakPreservation(this TextWrappingOptions options)
    {
        return options with { PreserveLineBreaks = false };
    }

    /// <summary>
    /// Sets a custom hyphenation dictionary.
    /// </summary>
    /// <param name="options">The text wrapping options</param>
    /// <param name="dictionary">Custom hyphenation dictionary for specific words</param>
    /// <returns>New options with custom hyphenation dictionary</returns>
    public static TextWrappingOptions WithCustomHyphenationDictionary(this TextWrappingOptions options, Dictionary<string, int[]> dictionary)
    {
        return options with { CustomHyphenationDictionary = dictionary };
    }
}
