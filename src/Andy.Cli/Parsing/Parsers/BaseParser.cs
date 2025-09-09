using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Andy.Cli.Services;

namespace Andy.Cli.Parsing.Parsers;

/// <summary>
/// Base parser with common functionality for all LLM parsers
/// </summary>
public abstract class BaseParser : ILlmResponseParser
{
    protected readonly ILogger? _logger;
    protected readonly IJsonRepairService _jsonRepair;

    // Common patterns for semantic extraction
    protected static readonly Regex FilePathPattern = new(
        @"(?:^|\s|[""\'\(])(?<path>(?:[a-zA-Z]:)?(?:[/\\][\w\-\.]+)+(?:\.\w+)?(?::\d+(?:-\d+)?)?)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    protected static readonly Regex CodeBlockPattern = new(
        @"```(?<lang>\w+)?\s*\n(?<code>.*?)\n```",
        RegexOptions.Compiled | RegexOptions.Singleline);

    protected static readonly Regex QuestionPattern = new(
        @"(?:^|\n)(?<question>(?:What|How|Why|When|Where|Who|Which|Would|Should|Can|Could|Do|Does|Is|Are)[^.!?]*\?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    protected static readonly Regex CommandPattern = new(
        @"(?:^|\n)\s*(?:\$|>|#)\s*(?<command>[\w\-]+(?:\s+[^\n]+)?)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    protected BaseParser(IJsonRepairService jsonRepair, ILogger? logger = null)
    {
        _jsonRepair = jsonRepair;
        _logger = logger;
    }

    public abstract ResponseNode Parse(string response, ParserContext? context = null);

    public virtual async Task<ResponseNode> ParseStreamingAsync(
        IAsyncEnumerable<string> chunks,
        ParserContext? context = null,
        CancellationToken cancellationToken = default)
    {
        // Default implementation: accumulate and parse
        var fullResponse = "";
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            fullResponse += chunk;
        }
        return Parse(fullResponse, context);
    }

    public virtual ValidationResult Validate(ResponseNode ast)
    {
        var issues = new List<ValidationIssue>();

        // Validate tool calls have required fields
        var toolCalls = FindNodes<ToolCallNode>(ast);
        foreach (var toolCall in toolCalls)
        {
            if (string.IsNullOrEmpty(toolCall.ToolName))
            {
                issues.Add(new ValidationIssue
                {
                    Message = "Tool call missing name",
                    Severity = ValidationSeverity.Error,
                    Node = toolCall
                });
            }

            if (string.IsNullOrEmpty(toolCall.CallId))
            {
                issues.Add(new ValidationIssue
                {
                    Message = "Tool call missing ID",
                    Severity = ValidationSeverity.Warning,
                    Node = toolCall
                });
            }
        }

        // Validate code blocks have language specified
        var codeBlocks = FindNodes<CodeNode>(ast);
        foreach (var code in codeBlocks)
        {
            if (string.IsNullOrEmpty(code.Language))
            {
                issues.Add(new ValidationIssue
                {
                    Message = "Code block missing language specification",
                    Severity = ValidationSeverity.Info,
                    Node = code
                });
            }
        }

        return issues.Any(i => i.Severity == ValidationSeverity.Error)
            ? ValidationResult.Failure(issues.ToArray())
            : ValidationResult.Success();
    }

    public abstract ParserCapabilities GetCapabilities();

    /// <summary>
    /// Extract semantic elements from text
    /// </summary>
    protected virtual void ExtractSemanticElements(ResponseNode root, string text, ParserContext? context)
    {
        if (context?.ExtractSemantics != true)
            return;

        // Extract file references
        var fileMatches = FilePathPattern.Matches(text);
        foreach (Match match in fileMatches)
        {
            var path = match.Groups["path"].Value;
            var fileRef = new FileReferenceNode
            {
                Path = path,
                IsAbsolute = path.StartsWith("/") || path.Contains(":"),
                ReferenceType = DetermineFileReferenceType(text, match.Index),
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length
            };

            // Check for line number reference
            if (path.Contains(':'))
            {
                var parts = path.Split(':');
                if (parts.Length > 1 && int.TryParse(parts[^1], out _))
                {
                    fileRef.Path = string.Join(':', parts[..^1]);
                    fileRef.LineReference = ":" + parts[^1];
                }
            }

            root.Children.Add(fileRef);
        }

        // Extract questions
        var questionMatches = QuestionPattern.Matches(text);
        foreach (Match match in questionMatches)
        {
            var question = new QuestionNode
            {
                Question = match.Groups["question"].Value.Trim(),
                Type = DetermineQuestionType(match.Groups["question"].Value),
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length
            };
            root.Children.Add(question);
        }

        // Extract commands
        var commandMatches = CommandPattern.Matches(text);
        foreach (Match match in commandMatches)
        {
            var command = new CommandNode
            {
                Command = match.Groups["command"].Value.Trim(),
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length
            };
            root.Children.Add(command);
        }
    }

    /// <summary>
    /// Extract code blocks from text
    /// </summary>
    protected virtual List<CodeNode> ExtractCodeBlocks(string text)
    {
        var codeBlocks = new List<CodeNode>();
        var matches = CodeBlockPattern.Matches(text);

        foreach (Match match in matches)
        {
            var codeBlock = new CodeNode
            {
                Language = match.Groups["lang"].Value.ToLowerInvariant(),
                Code = match.Groups["code"].Value,
                IsExecutable = IsExecutableLanguage(match.Groups["lang"].Value),
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length
            };

            // Try to extract filename from comment
            var firstLine = codeBlock.Code.Split('\n').FirstOrDefault() ?? "";
            if (firstLine.StartsWith("//") || firstLine.StartsWith("#"))
            {
                var fileMatch = Regex.Match(firstLine, @"(?:file:|filename:)?\s*([^\s]+\.\w+)");
                if (fileMatch.Success)
                {
                    codeBlock.FileName = fileMatch.Groups[1].Value;
                }
            }

            codeBlocks.Add(codeBlock);
        }

        return codeBlocks;
    }

    /// <summary>
    /// Find all nodes of a specific type in the AST
    /// </summary>
    protected List<T> FindNodes<T>(AstNode root) where T : AstNode
    {
        var results = new List<T>();
        TraverseNodes(root, node =>
        {
            if (node is T typedNode)
                results.Add(typedNode);
        });
        return results;
    }

    /// <summary>
    /// Traverse all nodes in the AST
    /// </summary>
    protected void TraverseNodes(AstNode node, Action<AstNode> action)
    {
        action(node);
        foreach (var child in node.Children)
        {
            TraverseNodes(child, action);
        }
    }

    private FileReferenceType DetermineFileReferenceType(string text, int position)
    {
        // Look at surrounding context to determine reference type
        var start = Math.Max(0, position - 50);
        var context = text.Substring(start, Math.Min(100, text.Length - start)).ToLower();

        if (context.Contains("create") || context.Contains("new file"))
            return FileReferenceType.Create;
        if (context.Contains("write") || context.Contains("save"))
            return FileReferenceType.Write;
        if (context.Contains("read") || context.Contains("open") || context.Contains("look at"))
            return FileReferenceType.Read;
        if (context.Contains("delete") || context.Contains("remove"))
            return FileReferenceType.Delete;
        if (context.Contains("modify") || context.Contains("edit") || context.Contains("change"))
            return FileReferenceType.Modify;
        if (context.Contains("navigate") || context.Contains("go to"))
            return FileReferenceType.Navigate;

        return FileReferenceType.Mention;
    }

    private QuestionType DetermineQuestionType(string question)
    {
        var lower = question.ToLower();

        if (lower.Contains("yes") || lower.Contains("no") ||
            lower.StartsWith("is ") || lower.StartsWith("are ") ||
            lower.StartsWith("do ") || lower.StartsWith("does ") ||
            lower.StartsWith("can ") || lower.StartsWith("could ") ||
            lower.StartsWith("should ") || lower.StartsWith("would "))
            return QuestionType.YesNo;

        if (lower.Contains("which") || lower.Contains("choose") || lower.Contains("option"))
            return QuestionType.MultipleChoice;

        if (lower.Contains("confirm") || lower.Contains("sure") || lower.Contains("ok"))
            return QuestionType.Confirmation;

        if (lower.Contains("clarify") || lower.Contains("mean") || lower.Contains("specifically"))
            return QuestionType.Clarification;

        return QuestionType.OpenEnded;
    }

    private bool IsExecutableLanguage(string language)
    {
        var lang = language.ToLower();
        return lang is "bash" or "sh" or "shell" or "powershell" or "cmd" or "bat" or "python" or "javascript" or "ruby";
    }
}