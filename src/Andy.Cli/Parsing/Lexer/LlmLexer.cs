using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Parsing.Lexer;

/// <summary>
/// Lexical analyzer for LLM responses - converts raw text to tokens
/// Similar to Roslyn's approach, continues even with errors
/// </summary>
public class LlmLexer
{
    private readonly ILogger<LlmLexer>? _logger;
    private readonly List<TokenPattern> _patterns;
    private readonly List<LexicalError> _errors = new();

    // Pre-compiled regex patterns for efficiency
    private static readonly Dictionary<TokenType, Regex> CompiledPatterns = new()
    {
        [TokenType.FilePath] = new(@"(?:[a-zA-Z]:)?(?:[/\\][\w\-\.]+)+(?:\.\w+)?(?::\d+(?:-\d+)?)?", RegexOptions.Compiled),
        [TokenType.Url] = new(@"https?://[^\s<>\""\{\}|\\^`\[\]]+", RegexOptions.Compiled),
        [TokenType.EmailAddress] = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled),
        [TokenType.CodeBlockStart] = new(@"```(\w+)?", RegexOptions.Compiled),
        [TokenType.CodeBlockEnd] = new(@"```", RegexOptions.Compiled),
        [TokenType.InlineCode] = new(@"`([^`]+)`", RegexOptions.Compiled),
        [TokenType.MarkdownHeader] = new(@"^#{1,6}\s+", RegexOptions.Compiled | RegexOptions.Multiline),
        [TokenType.MarkdownBold] = new(@"\*\*([^*]+)\*\*", RegexOptions.Compiled),
        [TokenType.MarkdownItalic] = new(@"\*([^*]+)\*", RegexOptions.Compiled),
        [TokenType.Question] = new(@"(?:What|How|Why|When|Where|Who|Which|Would|Should|Can|Could|Do|Does|Is|Are)[^.!?]*\?", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        [TokenType.Command] = new(@"^\s*[$>#]\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline),
        [TokenType.JsonObjectStart] = new(@"\{", RegexOptions.Compiled),
        [TokenType.JsonObjectEnd] = new(@"\}", RegexOptions.Compiled),
        [TokenType.JsonArrayStart] = new(@"\[", RegexOptions.Compiled),
        [TokenType.JsonArrayEnd] = new(@"\]", RegexOptions.Compiled),
    };

    public LlmLexer(ILogger<LlmLexer>? logger = null)
    {
        _logger = logger;
        _patterns = InitializePatterns();
    }

    /// <summary>
    /// Tokenize input text into a stream of tokens
    /// Continues even when encountering errors (like Roslyn)
    /// </summary>
    public LexerResult Tokenize(string input)
    {
        var tokens = new List<Token>();
        var position = 0;
        var line = 1;
        var column = 1;

        _errors.Clear();

        while (position < input.Length)
        {
            var matched = false;
            var bestMatch = (Token?)null;
            var bestLength = 0;

            // Try to match patterns at current position
            foreach (var pattern in _patterns.OrderByDescending(p => p.Priority))
            {
                var (token, length) = TryMatchPattern(input, position, line, column, pattern);
                if (token != null && length > bestLength)
                {
                    bestMatch = token;
                    bestLength = length;
                    matched = true;
                }
            }

            if (matched && bestMatch != null)
            {
                tokens.Add(bestMatch);
                UpdatePosition(input, ref position, ref line, ref column, bestLength);
            }
            else
            {
                // Error recovery: create unknown token and continue
                var unknownChar = input[position].ToString();
                var unknownToken = new Token(TokenType.Unknown, unknownChar, position)
                {
                    Line = line,
                    Column = column
                };

                tokens.Add(unknownToken);

                _errors.Add(new LexicalError
                {
                    Position = position,
                    Line = line,
                    Column = column,
                    Message = $"Unexpected character: '{unknownChar}'",
                    Context = GetContext(input, position)
                });

                UpdatePosition(input, ref position, ref line, ref column, 1);
            }
        }

        // Add EOF token
        tokens.Add(new Token(TokenType.Eof, "", position)
        {
            Line = line,
            Column = column
        });

        return new LexerResult
        {
            Tokens = tokens,
            Errors = _errors.ToList(),
            Success = !_errors.Any(e => e.Severity == ErrorSeverity.Error)
        };
    }

    /// <summary>
    /// Incremental tokenization for streaming scenarios
    /// </summary>
    public IEnumerable<Token> TokenizeIncremental(string chunk, LexerState state)
    {
        // Similar to how Roslyn handles incremental parsing
        var fullText = state.Buffer + chunk;
        var tokens = new List<Token>();

        // Try to tokenize as much as possible
        var result = Tokenize(fullText);

        // Determine which tokens are complete
        foreach (var token in result.Tokens)
        {
            if (token.Type == TokenType.Eof)
                continue;

            // Check if token is likely complete
            if (IsTokenComplete(token, fullText))
            {
                yield return token;
                state.LastCompletePosition = token.Position + token.Length;
            }
            else
            {
                // Keep incomplete tokens in buffer
                break;
            }
        }

        // Update buffer with remaining text
        state.Buffer = fullText.Substring(state.LastCompletePosition);
    }

    private (Token?, int) TryMatchPattern(string input, int position, int line, int column, TokenPattern pattern)
    {
        var remaining = input.Substring(position);

        if (pattern.CustomTokenizer != null)
        {
            try
            {
                var token = pattern.CustomTokenizer(remaining);
                if (token != null)
                {
                    token.Position = position;
                    token.Line = line;
                    token.Column = column;
                    return (token, token.Length);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Custom tokenizer failed for {Type}", pattern.Type);
            }
        }

        if (pattern.IsRegex && CompiledPatterns.TryGetValue(pattern.Type, out var regex))
        {
            var match = regex.Match(remaining);
            if (match.Success && match.Index == 0)
            {
                var token = new Token(pattern.Type, match.Value, position)
                {
                    Line = line,
                    Column = column
                };

                // Extract metadata for certain token types
                ExtractTokenMetadata(token, match);

                return (token, match.Length);
            }
        }
        else if (!pattern.IsRegex)
        {
            if (remaining.StartsWith(pattern.Pattern))
            {
                var token = new Token(pattern.Type, pattern.Pattern, position)
                {
                    Line = line,
                    Column = column
                };
                return (token, pattern.Pattern.Length);
            }
        }

        return (null, 0);
    }

    private void ExtractTokenMetadata(Token token, Match match)
    {
        switch (token.Type)
        {
            case TokenType.FilePath:
                // Extract line number if present
                if (token.Value.Contains(':'))
                {
                    var parts = token.Value.Split(':');
                    if (parts.Length > 1 && int.TryParse(parts[^1], out var lineNum))
                    {
                        token.Metadata = new { LineNumber = lineNum };
                    }
                }
                break;

            case TokenType.CodeBlockStart:
                // Extract language
                if (match.Groups.Count > 1)
                {
                    token.Metadata = new { Language = match.Groups[1].Value };
                }
                break;
        }
    }

    private void UpdatePosition(string input, ref int position, ref int line, ref int column, int length)
    {
        for (int i = 0; i < length && position < input.Length; i++)
        {
            if (input[position] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
            position++;
        }
    }

    private bool IsTokenComplete(Token token, string fullText)
    {
        // Heuristics to determine if a token is complete
        switch (token.Type)
        {
            case TokenType.CodeBlockStart:
                // Need matching end
                var codeBlockEnd = fullText.IndexOf("```", token.Position + token.Length);
                return codeBlockEnd != -1;

            case TokenType.JsonObjectStart:
                // Need balanced braces
                return IsJsonBalanced(fullText, token.Position);

            default:
                return true;
        }
    }

    private bool IsJsonBalanced(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escape = false;

        for (int i = start; i < text.Length; i++)
        {
            if (escape)
            {
                escape = false;
                continue;
            }

            var c = text[i];

            if (c == '\\')
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (c == '{' || c == '[')
                    depth++;
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0)
                        return true;
                }
            }
        }

        return false;
    }

    private string GetContext(string input, int position, int contextSize = 20)
    {
        var start = Math.Max(0, position - contextSize);
        var end = Math.Min(input.Length, position + contextSize);
        return input.Substring(start, end - start);
    }

    private List<TokenPattern> InitializePatterns()
    {
        // Priority: higher = matched first
        return new List<TokenPattern>
        {
            // Tool calls (highest priority)
            new() { Type = TokenType.ToolCallStart, Pattern = @"\{[^}]*""tool_call""", IsRegex = true, Priority = 100 },
            
            // Code blocks
            new() { Type = TokenType.CodeBlockStart, Pattern = "```", IsRegex = true, Priority = 90 },
            
            // Semantic elements
            new() { Type = TokenType.FilePath, Pattern = "", IsRegex = true, Priority = 80 },
            new() { Type = TokenType.Url, Pattern = "", IsRegex = true, Priority = 79 },
            new() { Type = TokenType.EmailAddress, Pattern = "", IsRegex = true, Priority = 78 },
            
            // JSON structures
            new() { Type = TokenType.JsonObjectStart, Pattern = "{", IsRegex = false, Priority = 70 },
            new() { Type = TokenType.JsonObjectEnd, Pattern = "}", IsRegex = false, Priority = 70 },
            
            // Markdown
            new() { Type = TokenType.MarkdownHeader, Pattern = "", IsRegex = true, Priority = 60 },
            new() { Type = TokenType.MarkdownBold, Pattern = "", IsRegex = true, Priority = 59 },
            
            // Questions and commands
            new() { Type = TokenType.Question, Pattern = "", IsRegex = true, Priority = 50 },
            new() { Type = TokenType.Command, Pattern = "", IsRegex = true, Priority = 49 },
            
            // Basic text (lowest priority)
            new() { Type = TokenType.Text, Pattern = "", CustomTokenizer = TextTokenizer, Priority = 1 }
        };
    }

    private Token? TextTokenizer(string input)
    {
        // Match any text until a special character or pattern
        var sb = new StringBuilder();
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i];

            // Stop at potential special patterns
            if (c == '`' || c == '{' || c == '[' || c == '*' || c == '#')
                break;

            // Stop at newlines for better incremental processing
            if (c == '\n')
            {
                if (sb.Length > 0)
                    break;
                return new Token(TokenType.Newline, "\n", 0) { Length = 1 };
            }

            sb.Append(c);
            i++;
        }

        if (sb.Length > 0)
        {
            return new Token(TokenType.Text, sb.ToString(), 0);
        }

        return null;
    }
}

/// <summary>
/// Result of lexical analysis
/// </summary>
public class LexerResult
{
    public List<Token> Tokens { get; set; } = new();
    public List<LexicalError> Errors { get; set; } = new();
    public bool Success { get; set; }
}

/// <summary>
/// Lexical error information
/// </summary>
public class LexicalError
{
    public int Position { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public string Message { get; set; } = "";
    public string Context { get; set; } = "";
    public ErrorSeverity Severity { get; set; } = ErrorSeverity.Warning;
}

/// <summary>
/// State for incremental lexing
/// </summary>
public class LexerState
{
    public string Buffer { get; set; } = "";
    public int LastCompletePosition { get; set; }
    public List<Token> PendingTokens { get; set; } = new();
}

public enum ErrorSeverity
{
    Info,
    Warning,
    Error
}