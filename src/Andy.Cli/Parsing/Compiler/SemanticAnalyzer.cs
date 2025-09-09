using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Parsing.Compiler;

/// <summary>
/// Semantic analyzer for LLM responses
/// Similar to Roslyn's semantic analysis, checks for logical errors and inconsistencies
/// </summary>
public class SemanticAnalyzer
{
    private readonly ILogger<SemanticAnalyzer>? _logger;
    private readonly Dictionary<string, ToolDefinition> _knownTools;

    public SemanticAnalyzer(ILogger<SemanticAnalyzer>? logger = null)
    {
        _logger = logger;
        _knownTools = InitializeKnownTools();
    }

    /// <summary>
    /// Perform semantic analysis on the AST
    /// </summary>
    public SemanticAnalysisResult Analyze(ResponseNode ast, CompilerOptions options)
    {
        var result = new SemanticAnalysisResult();
        var context = new AnalysisContext();

        try
        {
            // Check for duplicate tool calls
            CheckDuplicateToolCalls(ast, result, context);

            // Validate tool call arguments
            ValidateToolCallArguments(ast, result, context);

            // Check for orphaned tool responses
            CheckOrphanedToolResponses(ast, result, context);

            // Validate file references
            ValidateFileReferences(ast, result, context);

            // Check for conflicting operations
            CheckConflictingOperations(ast, result, context);

            // Analyze control flow
            AnalyzeControlFlow(ast, result, context);

            // Check for incomplete code blocks
            CheckIncompleteCodeBlocks(ast, result, context);

            // Validate questions and expected responses
            ValidateQuestions(ast, result, context);

            // Extract semantic information
            ExtractSemanticInfo(ast, result, context);

            result.Success = !result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Semantic analysis failed");
            result.Success = false;
            result.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"Semantic analysis error: {ex.Message}",
                Phase = CompilationPhase.Semantic
            });
        }

        return result;
    }

    private void CheckDuplicateToolCalls(ResponseNode ast, SemanticAnalysisResult result, AnalysisContext context)
    {
        var toolCalls = ast.Children.OfType<ToolCallNode>().ToList();
        var seenCalls = new Dictionary<string, ToolCallNode>();

        foreach (var toolCall in toolCalls)
        {
            var signature = GetToolCallSignature(toolCall);

            if (seenCalls.TryGetValue(signature, out var previousCall))
            {
                result.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"Duplicate tool call to '{toolCall.ToolName}' with same arguments",
                    Node = toolCall,
                    Phase = CompilationPhase.Semantic
                });

                context.DuplicateToolCalls.Add((previousCall, toolCall));
            }
            else
            {
                seenCalls[signature] = toolCall;
            }
        }
    }

    private void ValidateToolCallArguments(ResponseNode ast, SemanticAnalysisResult result, AnalysisContext context)
    {
        var toolCalls = ast.Children.OfType<ToolCallNode>();

        foreach (var toolCall in toolCalls)
        {
            if (_knownTools.TryGetValue(toolCall.ToolName.ToLower(), out var toolDef))
            {
                // Check required parameters
                foreach (var required in toolDef.RequiredParameters)
                {
                    if (!toolCall.Arguments.ContainsKey(required))
                    {
                        result.Diagnostics.Add(new Diagnostic
                        {
                            Severity = DiagnosticSeverity.Error,
                            Message = $"Tool '{toolCall.ToolName}' missing required parameter '{required}'",
                            Node = toolCall,
                            Phase = CompilationPhase.Semantic
                        });
                    }
                }

                // Check parameter types
                foreach (var arg in toolCall.Arguments)
                {
                    if (toolDef.ParameterTypes.TryGetValue(arg.Key, out var expectedType))
                    {
                        if (!IsValidType(arg.Value, expectedType))
                        {
                            result.Diagnostics.Add(new Diagnostic
                            {
                                Severity = DiagnosticSeverity.Warning,
                                Message = $"Tool '{toolCall.ToolName}' parameter '{arg.Key}' has incorrect type. Expected {expectedType}",
                                Node = toolCall,
                                Phase = CompilationPhase.Semantic
                            });
                        }
                    }
                }
            }
            else
            {
                // Unknown tool
                result.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Info,
                    Message = $"Unknown tool '{toolCall.ToolName}'",
                    Node = toolCall,
                    Phase = CompilationPhase.Semantic
                });
            }
        }
    }

    private void CheckOrphanedToolResponses(ResponseNode ast, SemanticAnalysisResult result, AnalysisContext context)
    {
        // This would check for tool responses without corresponding calls
        // Similar to the orphaned cleanup in EnhancedContextManager
    }

    private void ValidateFileReferences(ResponseNode ast, SemanticAnalysisResult result, AnalysisContext context)
    {
        var fileRefs = ast.Children.OfType<FileReferenceNode>().ToList();
        var fileOperations = new Dictionary<string, List<FileReferenceNode>>();

        foreach (var fileRef in fileRefs)
        {
            if (!fileOperations.ContainsKey(fileRef.Path))
            {
                fileOperations[fileRef.Path] = new List<FileReferenceNode>();
            }
            fileOperations[fileRef.Path].Add(fileRef);
        }

        // Check for conflicting operations on same file
        foreach (var (path, refs) in fileOperations)
        {
            var hasDelete = refs.Any(r => r.ReferenceType == FileReferenceType.Delete);
            var hasWrite = refs.Any(r => r.ReferenceType == FileReferenceType.Write ||
                                        r.ReferenceType == FileReferenceType.Create);

            if (hasDelete && hasWrite)
            {
                result.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"Conflicting operations on file '{path}': both delete and write/create",
                    Phase = CompilationPhase.Semantic
                });
            }

            // Check for multiple creates
            var creates = refs.Where(r => r.ReferenceType == FileReferenceType.Create).ToList();
            if (creates.Count > 1)
            {
                result.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"Multiple create operations for file '{path}'",
                    Node = creates[1],
                    Phase = CompilationPhase.Semantic
                });
            }
        }

        context.FileOperations = fileOperations;
    }

    private void CheckConflictingOperations(ResponseNode ast, SemanticAnalysisResult result, AnalysisContext context)
    {
        // Check for logically conflicting operations
        var commands = ast.Children.OfType<CommandNode>().ToList();

        // Check for cd commands followed by relative path operations
        for (int i = 0; i < commands.Count - 1; i++)
        {
            if (commands[i].Command.StartsWith("cd "))
            {
                if (commands[i + 1].Command.Contains("./") || !commands[i + 1].Command.Contains("/"))
                {
                    result.Diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Info,
                        Message = "Command uses relative path after directory change",
                        Node = commands[i + 1],
                        Phase = CompilationPhase.Semantic
                    });
                }
            }
        }
    }

    private void AnalyzeControlFlow(ResponseNode ast, SemanticAnalysisResult result, AnalysisContext context)
    {
        // Analyze the logical flow of operations
        var operations = new List<(AstNode node, string type)>();

        foreach (var child in ast.Children)
        {
            operations.Add(child switch
            {
                ToolCallNode => (child, "tool"),
                CommandNode => (child, "command"),
                CodeNode => (child, "code"),
                QuestionNode => (child, "question"),
                _ => (child, "other")
            });
        }

        // Check for questions after operations (might not get answered)
        for (int i = 0; i < operations.Count - 1; i++)
        {
            if (operations[i].type == "tool" || operations[i].type == "command")
            {
                if (operations[i + 1].type == "question")
                {
                    result.Diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Info,
                        Message = "Question follows operation - response may be delayed",
                        Node = operations[i + 1].node,
                        Phase = CompilationPhase.Semantic
                    });
                }
            }
        }

        context.OperationFlow = operations;
    }

    private void CheckIncompleteCodeBlocks(ResponseNode ast, SemanticAnalysisResult result, AnalysisContext context)
    {
        var codeBlocks = ast.Children.OfType<CodeNode>();

        foreach (var code in codeBlocks)
        {
            // Check for common indicators of incomplete code
            if (code.Code.EndsWith("...") || code.Code.Contains("// TODO") || code.Code.Contains("# TODO"))
            {
                result.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Info,
                    Message = "Code block appears incomplete",
                    Node = code,
                    Phase = CompilationPhase.Semantic
                });
            }

            // Check for unbalanced braces/brackets
            if (!IsBalanced(code.Code))
            {
                result.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = "Code block has unbalanced braces or brackets",
                    Node = code,
                    Phase = CompilationPhase.Semantic
                });
            }
        }
    }

    private void ValidateQuestions(ResponseNode ast, SemanticAnalysisResult result, AnalysisContext context)
    {
        var questions = ast.Children.OfType<QuestionNode>().ToList();

        foreach (var question in questions)
        {
            // Check if question expects specific format
            if (question.Type == QuestionType.YesNo && question.SuggestedOptions == null)
            {
                question.SuggestedOptions = new List<string> { "Yes", "No" };
            }

            context.Questions.Add(question);
        }

        if (questions.Count > 3)
        {
            result.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Info,
                Message = $"Response contains {questions.Count} questions - consider reducing for clarity",
                Phase = CompilationPhase.Semantic
            });
        }
    }

    private void ExtractSemanticInfo(ResponseNode ast, SemanticAnalysisResult result, AnalysisContext context)
    {
        // Extract high-level semantic information
        result.ExtractedInfo = new SemanticInfo
        {
            HasToolCalls = ast.Children.Any(c => c is ToolCallNode),
            HasCode = ast.Children.Any(c => c is CodeNode),
            HasQuestions = ast.Children.Any(c => c is QuestionNode),
            HasErrors = ast.Children.Any(c => c is ErrorNode),
            TotalNodes = CountNodes(ast),
            FileReferences = context.FileOperations.Keys.ToList(),
            ToolsUsed = ast.Children.OfType<ToolCallNode>().Select(t => t.ToolName).Distinct().ToList(),
            PrimaryIntent = DeterminePrimaryIntent(ast)
        };
    }

    private string GetToolCallSignature(ToolCallNode toolCall)
    {
        var args = System.Text.Json.JsonSerializer.Serialize(toolCall.Arguments);
        return $"{toolCall.ToolName}:{args}";
    }

    private bool IsValidType(object? value, string expectedType)
    {
        return expectedType.ToLower() switch
        {
            "string" => value is string,
            "number" or "int" or "integer" => value is int or long or double or float,
            "boolean" or "bool" => value is bool,
            "array" => value is Array or System.Collections.IEnumerable,
            "object" => value is not null,
            _ => true
        };
    }

    private bool IsBalanced(string code)
    {
        var stack = new Stack<char>();
        var pairs = new Dictionary<char, char>
        {
            { '(', ')' },
            { '[', ']' },
            { '{', '}' }
        };

        var inString = false;
        var inChar = false;
        var escape = false;

        foreach (var c in code)
        {
            if (escape)
            {
                escape = false;
                continue;
            }

            if (c == '\\')
            {
                escape = true;
                continue;
            }

            if (c == '"' && !inChar)
                inString = !inString;
            else if (c == '\'' && !inString)
                inChar = !inChar;

            if (!inString && !inChar)
            {
                if (pairs.ContainsKey(c))
                    stack.Push(c);
                else if (pairs.ContainsValue(c))
                {
                    if (stack.Count == 0 || pairs[stack.Pop()] != c)
                        return false;
                }
            }
        }

        return stack.Count == 0;
    }

    private int CountNodes(AstNode node)
    {
        var count = 1;
        foreach (var child in node.Children)
        {
            count += CountNodes(child);
        }
        return count;
    }

    private string DeterminePrimaryIntent(ResponseNode ast)
    {
        var typeCounts = new Dictionary<string, int>();

        foreach (var child in ast.Children)
        {
            var type = child switch
            {
                ToolCallNode => "ToolExecution",
                CodeNode => "CodeGeneration",
                QuestionNode => "Clarification",
                ErrorNode => "ErrorReporting",
                CommandNode => "CommandExecution",
                TextNode => "Explanation",
                _ => "Other"
            };

            typeCounts[type] = typeCounts.GetValueOrDefault(type, 0) + 1;
        }

        return typeCounts.Any()
            ? typeCounts.OrderByDescending(kvp => kvp.Value).First().Key
            : "Unknown";
    }

    private Dictionary<string, ToolDefinition> InitializeKnownTools()
    {
        return new Dictionary<string, ToolDefinition>
        {
            ["write_file"] = new ToolDefinition
            {
                Name = "write_file",
                RequiredParameters = new[] { "path", "content" },
                ParameterTypes = new Dictionary<string, string>
                {
                    ["path"] = "string",
                    ["content"] = "string"
                }
            },
            ["read_file"] = new ToolDefinition
            {
                Name = "read_file",
                RequiredParameters = new[] { "path" },
                ParameterTypes = new Dictionary<string, string>
                {
                    ["path"] = "string"
                }
            },
            ["list_directory"] = new ToolDefinition
            {
                Name = "list_directory",
                RequiredParameters = new[] { "path" },
                ParameterTypes = new Dictionary<string, string>
                {
                    ["path"] = "string",
                    ["recursive"] = "boolean"
                }
            },
            ["execute_command"] = new ToolDefinition
            {
                Name = "execute_command",
                RequiredParameters = new[] { "command" },
                ParameterTypes = new Dictionary<string, string>
                {
                    ["command"] = "string",
                    ["working_directory"] = "string"
                }
            }
        };
    }
}

/// <summary>
/// Result of semantic analysis
/// </summary>
public class SemanticAnalysisResult
{
    public bool Success { get; set; }
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public SemanticInfo? ExtractedInfo { get; set; }
}

/// <summary>
/// Extracted semantic information
/// </summary>
public class SemanticInfo
{
    public bool HasToolCalls { get; set; }
    public bool HasCode { get; set; }
    public bool HasQuestions { get; set; }
    public bool HasErrors { get; set; }
    public int TotalNodes { get; set; }
    public List<string> FileReferences { get; set; } = new();
    public List<string> ToolsUsed { get; set; } = new();
    public string PrimaryIntent { get; set; } = "";
}

/// <summary>
/// Context for semantic analysis
/// </summary>
public class AnalysisContext
{
    public List<(ToolCallNode, ToolCallNode)> DuplicateToolCalls { get; set; } = new();
    public Dictionary<string, List<FileReferenceNode>> FileOperations { get; set; } = new();
    public List<(AstNode node, string type)> OperationFlow { get; set; } = new();
    public List<QuestionNode> Questions { get; set; } = new();
}

/// <summary>
/// Tool definition for validation
/// </summary>
public class ToolDefinition
{
    public string Name { get; set; } = "";
    public string[] RequiredParameters { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> ParameterTypes { get; set; } = new();
}