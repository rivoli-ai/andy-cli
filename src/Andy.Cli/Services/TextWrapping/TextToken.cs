using System;
using System.Collections.Generic;

namespace Andy.Cli.Services.TextWrapping;

/// <summary>
/// Represents a token in the text (word, space, punctuation, etc.).
/// </summary>
public class TextToken
{
    /// <summary>
    /// The text content of the token.
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// The type of token.
    /// </summary>
    public TextTokenType Type { get; }

    /// <summary>
    /// The width of this token in characters.
    /// </summary>
    public int Width => Content.Length;

    /// <summary>
    /// Whether this token can be broken (for hyphenation).
    /// </summary>
    public bool CanBreak { get; }

    /// <summary>
    /// Hyphenation points within this token (character positions).
    /// </summary>
    public IReadOnlyList<int> HyphenationPoints { get; }

    public TextToken(string content, TextTokenType type, bool canBreak = false, IReadOnlyList<int>? hyphenationPoints = null)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Type = type;
        CanBreak = canBreak;
        HyphenationPoints = hyphenationPoints ?? Array.Empty<int>();
    }

    public override string ToString()
    {
        return $"{Type}: '{Content}'";
    }
}

/// <summary>
/// Types of text tokens.
/// </summary>
public enum TextTokenType
{
    /// <summary>
    /// Regular word characters.
    /// </summary>
    Word,

    /// <summary>
    /// Whitespace characters (spaces, tabs).
    /// </summary>
    Whitespace,

    /// <summary>
    /// Punctuation characters.
    /// </summary>
    Punctuation,

    /// <summary>
    /// Line break characters.
    /// </summary>
    LineBreak,

    /// <summary>
    /// Special characters (markdown, etc.).
    /// </summary>
    Special
}

/// <summary>
/// Represents a potential break point in the text.
/// </summary>
public class BreakPoint
{
    /// <summary>
    /// The token index where this break occurs.
    /// </summary>
    public int TokenIndex { get; }

    /// <summary>
    /// The character position within the token where the break occurs.
    /// </summary>
    public int CharacterPosition { get; }

    /// <summary>
    /// The penalty for breaking at this point (lower is better).
    /// </summary>
    public int Penalty { get; }

    /// <summary>
    /// Whether this break point uses hyphenation.
    /// </summary>
    public bool IsHyphenated { get; }

    /// <summary>
    /// The width of the line if broken at this point.
    /// </summary>
    public int LineWidth { get; }

    public BreakPoint(int tokenIndex, int characterPosition, int penalty, bool isHyphenated = false, int lineWidth = 0)
    {
        TokenIndex = tokenIndex;
        CharacterPosition = characterPosition;
        Penalty = penalty;
        IsHyphenated = isHyphenated;
        LineWidth = lineWidth;
    }

    public override string ToString()
    {
        return $"Break at token {TokenIndex}, pos {CharacterPosition}, penalty {Penalty}";
    }
}

/// <summary>
/// Represents a line in the wrapped text.
/// </summary>
public class WrappedLine
{
    /// <summary>
    /// The tokens that make up this line.
    /// </summary>
    public IReadOnlyList<TextToken> Tokens { get; }

    /// <summary>
    /// The text content of this line.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// The width of this line in characters.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Whether this line ends with hyphenation.
    /// </summary>
    public bool EndsWithHyphenation { get; }

    public WrappedLine(IReadOnlyList<TextToken> tokens, string text, int width, bool endsWithHyphenation = false)
    {
        Tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Width = width;
        EndsWithHyphenation = endsWithHyphenation;
    }
}
