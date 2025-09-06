using System;

namespace Andy.Cli.Parsing.Lexer;

/// <summary>
/// Token representation for LLM response lexical analysis
/// </summary>
public class Token
{
    public TokenType Type { get; set; }
    public string Value { get; set; } = "";
    public int Position { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; }
    public object? Metadata { get; set; }
    
    public Token(TokenType type, string value, int position)
    {
        Type = type;
        Value = value;
        Position = position;
        Length = value.Length;
    }
    
    public override string ToString() => $"{Type}({Value})@{Position}";
}

/// <summary>
/// Token types for LLM response lexical analysis
/// </summary>
public enum TokenType
{
    // Structural tokens
    Text,
    Whitespace,
    Newline,
    
    // Code and markup
    CodeBlockStart,
    CodeBlockEnd,
    CodeBlockLanguage,
    InlineCode,
    
    // Tool calls
    ToolCallStart,
    ToolCallEnd,
    ToolName,
    ToolArgument,
    
    // JSON tokens
    JsonObjectStart,
    JsonObjectEnd,
    JsonArrayStart,
    JsonArrayEnd,
    JsonProperty,
    JsonValue,
    JsonString,
    JsonNumber,
    JsonBoolean,
    JsonNull,
    
    // Markdown tokens
    MarkdownHeader,
    MarkdownBold,
    MarkdownItalic,
    MarkdownListItem,
    MarkdownLink,
    MarkdownImage,
    MarkdownBlockquote,
    
    // Semantic tokens
    FilePath,
    FileName,
    LineNumber,
    Url,
    EmailAddress,
    Question,
    Command,
    
    // Special markers
    ThoughtStart,
    ThoughtEnd,
    ErrorMarker,
    WarningMarker,
    
    // Punctuation
    Period,
    Comma,
    Colon,
    Semicolon,
    QuestionMark,
    ExclamationMark,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    OpenBrace,
    CloseBrace,
    Quote,
    DoubleQuote,
    
    // Control tokens
    Eof,
    Unknown
}

/// <summary>
/// Token pattern for matching
/// </summary>
public class TokenPattern
{
    public TokenType Type { get; set; }
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public int Priority { get; set; }
    public Func<string, Token>? CustomTokenizer { get; set; }
}