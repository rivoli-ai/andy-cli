using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Andy.Cli.Parsing.Lexer;
using Andy.Cli.Parsing.Parsers;
using Andy.Cli.Services;
// using Andy.Llm.Parsing;
// using Andy.Llm.Parsing.Ast;
// Temporarily disabled while we resolve interface compatibility issues

namespace Andy.Cli.Parsing.Compiler;

/// <summary>
/// Full compiler pipeline for LLM responses
/// Similar to Roslyn's approach with multiple phases and error recovery
/// </summary>
public class LlmResponseCompiler
{
    private readonly ILogger<LlmResponseCompiler>? _logger;
    private readonly LlmLexer _lexer;
    private readonly ILlmResponseParser _parser;
    private readonly SemanticAnalyzer _semanticAnalyzer;
    private readonly IJsonRepairService _jsonRepair;

    public CompilationResult LastCompilationResult { get; private set; } = new();

    public LlmResponseCompiler(
        string modelProvider,
        IJsonRepairService jsonRepair,
        ILogger<LlmResponseCompiler>? logger = null)
    {
        _logger = logger;
        _jsonRepair = jsonRepair;
        _lexer = new LlmLexer(null);
        _parser = CreateParserForModel(modelProvider, jsonRepair, logger);
        _semanticAnalyzer = new SemanticAnalyzer(null);
    }

    /// <summary>
    /// Compile a complete response through all phases
    /// </summary>
    public CompilationResult Compile(string response, CompilerOptions? options = null)
    {
        options ??= new CompilerOptions();
        var result = new CompilationResult();
        var startTime = DateTime.UtcNow;

        try
        {
            // Phase 1: Lexical Analysis
            _logger?.LogDebug("Phase 1: Lexical analysis");
            var lexerResult = _lexer.Tokenize(response);
            result.Tokens = lexerResult.Tokens;
            result.Diagnostics.AddRange(ConvertLexicalErrors(lexerResult.Errors));

            if (options.StopOnLexicalErrors && lexerResult.Errors.Any(e => e.Severity == Lexer.ErrorSeverity.Error))
            {
                result.Success = false;
                result.CompilationTime = DateTime.UtcNow - startTime;
                return result;
            }

            // Phase 2: Parsing (Syntax Analysis)
            _logger?.LogDebug("Phase 2: Parsing");
            var context = new ParserContext
            {
                ModelProvider = options.ModelProvider,
                ModelName = options.ModelName,
                StrictMode = options.StrictMode,
                PreserveThoughts = options.PreserveThoughts
            };

            result.Ast = _parser.Parse(response, context);

            // Phase 3: Semantic Analysis
            _logger?.LogDebug("Phase 3: Semantic analysis");
            var semanticResult = _semanticAnalyzer.Analyze(result.Ast, options);
            result.Diagnostics.AddRange(semanticResult.Diagnostics);
            result.SemanticInfo = semanticResult;

            // Phase 4: Optimization (if enabled)
            if (options.EnableOptimizations)
            {
                _logger?.LogDebug("Phase 4: Optimization");
                result.Ast = Optimize(result.Ast, options);
            }

            // Phase 5: Validation
            _logger?.LogDebug("Phase 5: Validation");
            var validationResult = _parser.Validate(result.Ast);
            if (!validationResult.IsValid)
            {
                result.Diagnostics.AddRange(ConvertValidationIssues(validationResult.Issues));
            }

            result.Success = !result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
            result.CompilationTime = DateTime.UtcNow - startTime;

            LastCompilationResult = result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Compilation failed");
            result.Success = false;
            result.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"Internal compiler error: {ex.Message}",
                Phase = CompilationPhase.Unknown
            });
            result.CompilationTime = DateTime.UtcNow - startTime;
        }

        return result;
    }

    /// <summary>
    /// Incremental compilation for streaming scenarios
    /// Similar to Roslyn's incremental compilation
    /// </summary>
    public async Task<CompilationResult> CompileIncrementalAsync(
        IAsyncEnumerable<string> chunks,
        CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CompilerOptions();
        var state = new IncrementalCompilationState();
        var result = new CompilationResult();

        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            state.Buffer += chunk;

            // Try to compile what we have so far
            var partialResult = Compile(state.Buffer, options);

            // Emit incremental updates
            if (IncrementalUpdateAvailable != null)
            {
                var update = new IncrementalUpdate
                {
                    NewTokens = partialResult.Tokens.Skip(state.LastTokenCount).ToList(),
                    UpdatedAst = partialResult.Ast,
                    NewDiagnostics = partialResult.Diagnostics.Skip(state.LastDiagnosticCount).ToList()
                };

                IncrementalUpdateAvailable?.Invoke(update);
            }

            state.LastTokenCount = partialResult.Tokens.Count;
            state.LastDiagnosticCount = partialResult.Diagnostics.Count;
            result = partialResult;
        }

        return result;
    }

    /// <summary>
    /// Optimize the AST
    /// </summary>
    private ResponseNode Optimize(ResponseNode ast, CompilerOptions options)
    {
        // Remove duplicate tool calls
        RemoveDuplicateToolCalls(ast);

        // Merge adjacent text nodes
        MergeAdjacentTextNodes(ast);

        // Clean up empty nodes
        RemoveEmptyNodes(ast);

        // Normalize file paths
        if (options.NormalizeFilePaths)
        {
            NormalizeFilePaths(ast);
        }

        return ast;
    }

    private void RemoveDuplicateToolCalls(ResponseNode ast)
    {
        var seenCalls = new HashSet<string>();
        var toolCalls = ast.Children.OfType<ToolCallNode>().ToList();

        foreach (var toolCall in toolCalls)
        {
            var key = $"{toolCall.ToolName}:{System.Text.Json.JsonSerializer.Serialize(toolCall.Arguments)}";
            if (!seenCalls.Add(key))
            {
                ast.Children.Remove(toolCall);
                _logger?.LogDebug("Removed duplicate tool call: {ToolName}", toolCall.ToolName);
            }
        }
    }

    private void MergeAdjacentTextNodes(ResponseNode ast)
    {
        var textNodes = new List<TextNode>();
        var newChildren = new List<AstNode>();

        foreach (var child in ast.Children)
        {
            if (child is TextNode textNode)
            {
                textNodes.Add(textNode);
            }
            else
            {
                if (textNodes.Count > 0)
                {
                    var merged = MergeTextNodes(textNodes);
                    newChildren.Add(merged);
                    textNodes.Clear();
                }
                newChildren.Add(child);
            }
        }

        if (textNodes.Count > 0)
        {
            var merged = MergeTextNodes(textNodes);
            newChildren.Add(merged);
        }

        ast.Children = newChildren;
    }

    private TextNode MergeTextNodes(List<TextNode> nodes)
    {
        if (nodes.Count == 1)
            return nodes[0];

        return new TextNode
        {
            Content = string.Join(" ", nodes.Select(n => n.Content)),
            Format = nodes[0].Format,
            StartPosition = nodes[0].StartPosition,
            EndPosition = nodes[^1].EndPosition
        };
    }

    private void RemoveEmptyNodes(ResponseNode ast)
    {
        ast.Children.RemoveAll(child =>
            child is TextNode text && string.IsNullOrWhiteSpace(text.Content));
    }

    private void NormalizeFilePaths(ResponseNode ast)
    {
        var fileRefs = ast.Children.OfType<FileReferenceNode>();
        foreach (var fileRef in fileRefs)
        {
            // Normalize path separators
            fileRef.Path = fileRef.Path.Replace('\\', '/');

            // Resolve relative paths if possible
            if (!fileRef.IsAbsolute && !fileRef.Path.StartsWith("./"))
            {
                fileRef.Path = "./" + fileRef.Path;
            }
        }
    }

    private ILlmResponseParser CreateParserForModel(string modelProvider, IJsonRepairService jsonRepair, ILogger? logger)
    {
        var provider = modelProvider?.ToLowerInvariant() ?? "";
        
        // Use specific parsers for models with special formatting
        if (provider.Contains("qwen"))
        {
            return new QwenParser(jsonRepair, logger as ILogger<QwenParser>);
        }
        
        // For all other models (Llama, Mistral, GPT, Claude, etc.), use the generic parser
        // These models typically return plain text or simple JSON tool calls
        return new GenericParser(jsonRepair, logger as ILogger<GenericParser>);
    }

    private List<Diagnostic> ConvertLexicalErrors(List<LexicalError> errors)
    {
        return errors.Select(e => new Diagnostic
        {
            Severity = ConvertSeverity(e.Severity),
            Message = e.Message,
            Line = e.Line,
            Column = e.Column,
            Phase = CompilationPhase.Lexical
        }).ToList();
    }

    private List<Diagnostic> ConvertValidationIssues(List<ValidationIssue> issues)
    {
        return issues.Select(i => new Diagnostic
        {
            Severity = ConvertSeverity(i.Severity),
            Message = i.Message,
            Phase = CompilationPhase.Validation,
            Node = i.Node
        }).ToList();
    }

    private DiagnosticSeverity ConvertSeverity(Lexer.ErrorSeverity severity)
    {
        return severity switch
        {
            Lexer.ErrorSeverity.Error => DiagnosticSeverity.Error,
            Lexer.ErrorSeverity.Warning => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info
        };
    }

    private DiagnosticSeverity ConvertSeverity(ValidationSeverity severity)
    {
        return severity switch
        {
            ValidationSeverity.Error => DiagnosticSeverity.Error,
            ValidationSeverity.Warning => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info
        };
    }

    public event Action<IncrementalUpdate>? IncrementalUpdateAvailable;
}

/// <summary>
/// Compilation options
/// </summary>
public class CompilerOptions
{
    public string ModelProvider { get; set; } = "qwen";
    public string ModelName { get; set; } = "unknown";
    public bool StrictMode { get; set; } = false;
    public bool PreserveThoughts { get; set; } = false;
    public bool EnableOptimizations { get; set; } = true;
    public bool NormalizeFilePaths { get; set; } = true;
    public bool StopOnLexicalErrors { get; set; } = false;
}

/// <summary>
/// Compilation result with all phases
/// </summary>
public class CompilationResult
{
    public bool Success { get; set; }
    public List<Token> Tokens { get; set; } = new();
    public ResponseNode? Ast { get; set; }
    public SemanticAnalysisResult? SemanticInfo { get; set; }
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public TimeSpan CompilationTime { get; set; }
}

/// <summary>
/// Diagnostic information (errors, warnings, info)
/// </summary>
public class Diagnostic
{
    public DiagnosticSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public int? Line { get; set; }
    public int? Column { get; set; }
    public CompilationPhase Phase { get; set; }
    public AstNode? Node { get; set; }
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum CompilationPhase
{
    Unknown,
    Lexical,
    Parsing,
    Semantic,
    Optimization,
    Validation
}

/// <summary>
/// State for incremental compilation
/// </summary>
public class IncrementalCompilationState
{
    public string Buffer { get; set; } = "";
    public int LastTokenCount { get; set; }
    public int LastDiagnosticCount { get; set; }
}

/// <summary>
/// Incremental update notification
/// </summary>
public class IncrementalUpdate
{
    public List<Token> NewTokens { get; set; } = new();
    public ResponseNode? UpdatedAst { get; set; }
    public List<Diagnostic> NewDiagnostics { get; set; } = new();
}