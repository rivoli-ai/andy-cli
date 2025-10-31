using System;
using System.Collections.Generic;

namespace Andy.Cli.Services.TextWrapping;

/// <summary>
/// Options for text wrapping behavior.
/// </summary>
public record TextWrappingOptions
{
    /// <summary>
    /// Whether to prefer breaking at word boundaries over character boundaries.
    /// </summary>
    public bool PreferWordBoundaries { get; init; } = true;

    /// <summary>
    /// Whether to enable hyphenation for long words.
    /// </summary>
    public bool EnableHyphenation { get; init; } = true;

    /// <summary>
    /// Minimum length of word fragment when hyphenating (prevents very short fragments).
    /// </summary>
    public int MinHyphenationLength { get; init; } = 3;

    /// <summary>
    /// Maximum length of word fragment when hyphenating (prevents very long fragments).
    /// </summary>
    public int MaxHyphenationLength { get; init; } = 5;

    /// <summary>
    /// Custom hyphenation dictionary for specific words.
    /// </summary>
    public Dictionary<string, int[]>? CustomHyphenationDictionary { get; init; }

    /// <summary>
    /// The wrapping algorithm mode to use.
    /// </summary>
    public TextWrappingMode Mode { get; init; } = TextWrappingMode.Balanced;

    /// <summary>
    /// Whether to preserve existing line breaks in the input text.
    /// </summary>
    public bool PreserveLineBreaks { get; init; } = true;

    /// <summary>
    /// Whether to trim whitespace from wrapped lines.
    /// </summary>
    public bool TrimLines { get; init; } = true;
}

/// <summary>
/// Text wrapping algorithm modes.
/// </summary>
public enum TextWrappingMode
{
    /// <summary>
    /// Fast greedy algorithm - breaks at first available word boundary.
    /// </summary>
    Fast,

    /// <summary>
    /// Balanced approach - uses greedy with some optimization.
    /// </summary>
    Balanced,

    /// <summary>
    /// Optimal Knuth-Plass algorithm - finds globally optimal line breaks.
    /// </summary>
    Optimal
}

/// <summary>
/// Result of text wrapping operation.
/// </summary>
public class WrappedText
{
    /// <summary>
    /// The wrapped lines of text.
    /// </summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>
    /// Total number of lines in the wrapped text.
    /// </summary>
    public int LineCount => Lines.Count;

    /// <summary>
    /// Whether any hyphenation was used in the wrapping.
    /// </summary>
    public bool HasHyphenation { get; }

    /// <summary>
    /// Total width of the longest line.
    /// </summary>
    public int MaxLineWidth { get; }

    /// <summary>
    /// Creates a new WrappedText instance.
    /// </summary>
    public WrappedText(IReadOnlyList<string> lines, bool hasHyphenation = false)
    {
        Lines = lines ?? throw new ArgumentNullException(nameof(lines));
        HasHyphenation = hasHyphenation;
        MaxLineWidth = CalculateMaxLineWidth();
    }

    private int CalculateMaxLineWidth()
    {
        int maxWidth = 0;
        foreach (var line in Lines)
        {
            maxWidth = Math.Max(maxWidth, line.Length);
        }
        return maxWidth;
    }

    /// <summary>
    /// Returns the wrapped text as a single string with line breaks.
    /// </summary>
    public override string ToString()
    {
        return string.Join("\n", Lines);
    }
}
