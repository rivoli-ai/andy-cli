using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Parsing;

/// <summary>
/// Common interface for all LLM response parsers
/// </summary>
public interface ILlmResponseParser
{
    /// <summary>
    /// Parse a complete response into an AST
    /// </summary>
    ResponseNode Parse(string response, ParserContext? context = null);

    /// <summary>
    /// Parse streaming response chunks into an AST
    /// </summary>
    Task<ResponseNode> ParseStreamingAsync(
        IAsyncEnumerable<string> chunks,
        ParserContext? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate the AST for completeness and correctness
    /// </summary>
    ValidationResult Validate(ResponseNode ast);

    /// <summary>
    /// Get parser capabilities and supported features
    /// </summary>
    ParserCapabilities GetCapabilities();
}

/// <summary>
/// Context for parsing with model-specific settings
/// </summary>
public class ParserContext
{
    public string ModelProvider { get; set; } = "";
    public string ModelName { get; set; } = "";
    public bool StrictMode { get; set; } = false;
    public bool PreserveThoughts { get; set; } = false;
    public bool ExtractSemantics { get; set; } = true;
    public Dictionary<string, object> CustomSettings { get; set; } = new();
}

/// <summary>
/// Parser capabilities for feature discovery
/// </summary>
public class ParserCapabilities
{
    public bool SupportsStreaming { get; set; }
    public bool SupportsToolCalls { get; set; }
    public bool SupportsCodeBlocks { get; set; }
    public bool SupportsMarkdown { get; set; }
    public bool SupportsFileReferences { get; set; }
    public bool SupportsQuestions { get; set; }
    public bool SupportsThoughts { get; set; }
    public List<string> SupportedFormats { get; set; } = new();
}

/// <summary>
/// Validation result for parsed AST
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<ValidationIssue> Issues { get; set; } = new();

    public static ValidationResult Success() => new() { IsValid = true };

    public static ValidationResult Failure(params ValidationIssue[] issues) => new()
    {
        IsValid = false,
        Issues = new List<ValidationIssue>(issues)
    };
}

/// <summary>
/// Individual validation issue
/// </summary>
public class ValidationIssue
{
    public string Message { get; set; } = "";
    public ValidationSeverity Severity { get; set; }
    public AstNode? Node { get; set; }
    public string? Path { get; set; }
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}