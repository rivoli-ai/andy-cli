using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Andy.Cli.Services;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Parsing.Rendering;

/// <summary>
/// Renders AST back to displayable format with proper handling of tool results
/// </summary>
public class AstRenderer : IAstVisitor<string>
{
    private readonly ILogger<AstRenderer>? _logger;
    private readonly RenderOptions _options;
    private readonly StringBuilder _output = new();

    public AstRenderer(RenderOptions? options = null, ILogger<AstRenderer>? logger = null)
    {
        _options = options ?? new RenderOptions();
        _logger = logger;
    }

    /// <summary>
    /// Render an AST to string
    /// </summary>
    public string Render(ResponseNode ast)
    {
        _output.Clear();
        ast.Accept(this);
        return _output.ToString();
    }

    /// <summary>
    /// Render for streaming output with proper tool result display
    /// </summary>
    public StreamingRenderResult RenderForStreaming(ResponseNode ast)
    {
        var result = new StreamingRenderResult();

        // Separate tool calls from content
        var toolCalls = ast.Children.OfType<ToolCallNode>().ToList();
        var contentNodes = ast.Children.Where(n => n is not ToolCallNode and not ThoughtNode).ToList();

        // Render content
        foreach (var node in contentNodes)
        {
            var rendered = RenderNode(node);
            if (!string.IsNullOrWhiteSpace(rendered))
            {
                result.TextContent += rendered;
            }
        }

        // Extract tool calls for execution
        foreach (var toolCall in toolCalls)
        {
            result.ToolCalls.Add(new ModelToolCall
            {
                ToolId = toolCall.ToolName,
                Parameters = toolCall.Arguments
            });
        }

        result.HasContent = !string.IsNullOrWhiteSpace(result.TextContent);
        result.HasToolCalls = result.ToolCalls.Any();

        return result;
    }

    private string RenderNode(AstNode node)
    {
        return node.Accept(this);
    }

    public string VisitResponse(ResponseNode node)
    {
        var parts = new List<string>();

        foreach (var child in node.Children)
        {
            // Skip thoughts unless explicitly requested
            if (child is ThoughtNode && !_options.ShowThoughts)
                continue;

            var rendered = child.Accept(this);
            if (!string.IsNullOrWhiteSpace(rendered))
            {
                parts.Add(rendered);
            }
        }

        return string.Join(_options.NodeSeparator, parts);
    }

    public string VisitText(TextNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Content))
            return "";

        // Apply formatting based on text format
        return node.Format switch
        {
            TextFormat.Markdown => RenderMarkdown(node.Content),
            TextFormat.Json => _options.FormatJson ? FormatJson(node.Content) : node.Content,
            _ => node.Content
        };
    }

    public string VisitToolCall(ToolCallNode node)
    {
        if (!_options.ShowToolCalls)
            return "";

        // Don't render tool call JSON in the output - it's handled separately
        if (_options.ToolCallFormat == ToolCallDisplayFormat.Hidden)
            return "";

        if (_options.ToolCallFormat == ToolCallDisplayFormat.Summary)
        {
            return $"[Calling {node.ToolName}]";
        }

        // Full format
        var args = System.Text.Json.JsonSerializer.Serialize(node.Arguments, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        return $"Tool Call: {node.ToolName}\nArguments: {args}";
    }

    public string VisitToolResult(ToolResultNode node)
    {
        if (!_options.ShowToolResults)
            return "";

        // Don't render tool results in normal output - they're handled by the tool execution system
        if (_options.ToolResultFormat == ToolResultDisplayFormat.Hidden)
            return "";

        if (_options.ToolResultFormat == ToolResultDisplayFormat.Summary)
        {
            var status = node.IsSuccess ? "✅" : "❌";
            return $"[{node.ToolName} {(node.IsSuccess ? "completed" : "failed")}]";
        }

        // Full format
        var result = node.Result?.ToString() ?? "null";
        if (!string.IsNullOrEmpty(node.ErrorMessage))
        {
            result = $"Error: {node.ErrorMessage}";
        }

        return $"Tool Result ({node.ToolName}): {result}";
    }

    public string VisitCode(CodeNode node)
    {
        if (!_options.ShowCode)
            return "";

        var sb = new StringBuilder();

        // Add filename comment if available
        if (!string.IsNullOrEmpty(node.FileName))
        {
            sb.AppendLine($"// File: {node.FileName}");
        }

        // Format code block
        if (_options.UseCodeBlockMarkers)
        {
            sb.AppendLine($"```{node.Language}");
            sb.AppendLine(node.Code);
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine(node.Code);
        }

        return sb.ToString();
    }

    public string VisitFileReference(FileReferenceNode node)
    {
        if (!_options.HighlightFileReferences)
            return node.Path;

        // Format file reference with context
        var prefix = node.ReferenceType switch
        {
            FileReferenceType.Create => "📝 Create: ",
            FileReferenceType.Read => "📖 Read: ",
            FileReferenceType.Write => "✏️ Write: ",
            FileReferenceType.Delete => "🗑️ Delete: ",
            FileReferenceType.Modify => "📝 Modify: ",
            _ => ""
        };

        var path = node.Path;
        if (!string.IsNullOrEmpty(node.LineReference))
        {
            path += node.LineReference;
        }

        return _options.UseEmoji ? $"{prefix}{path}" : path;
    }

    public string VisitQuestion(QuestionNode node)
    {
        if (!_options.HighlightQuestions)
            return node.Question;

        var question = node.Question;

        // Add options if available
        if (node.SuggestedOptions?.Any() == true)
        {
            question += $" [{string.Join(" / ", node.SuggestedOptions)}]";
        }

        return _options.UseEmoji ? $"❓ {question}" : question;
    }

    public string VisitThought(ThoughtNode node)
    {
        if (!_options.ShowThoughts)
            return "";

        return $"[Thinking: {node.Content}]";
    }

    public string VisitMarkdown(MarkdownNode node)
    {
        return node.Element switch
        {
            MarkdownElement.Heading => new string('#', node.Level) + " ",
            MarkdownElement.ListItem => node.ListMarker ?? "- ",
            MarkdownElement.BlockQuote => "> ",
            _ => ""
        };
    }

    public string VisitError(ErrorNode node)
    {
        if (!_options.ShowErrors)
            return "";

        var prefix = node.Severity switch
        {
            ErrorSeverity.Critical => _options.UseEmoji ? "🔴" : "[CRITICAL]",
            ErrorSeverity.Error => _options.UseEmoji ? "❌" : "[ERROR]",
            ErrorSeverity.Warning => _options.UseEmoji ? "⚠️" : "[WARNING]",
            _ => _options.UseEmoji ? "ℹ️" : "[INFO]"
        };

        return $"{prefix} {node.Message}";
    }

    public string VisitCommand(CommandNode node)
    {
        if (!_options.ShowCommands)
            return node.Command;

        return _options.UseCodeBlockMarkers
            ? $"```bash\n{node.Command}\n```"
            : $"$ {node.Command}";
    }

    private string RenderMarkdown(string content)
    {
        // Basic markdown rendering
        return content;
    }

    private string FormatJson(string json)
    {
        try
        {
            var parsed = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(parsed, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch
        {
            return json;
        }
    }
}

/// <summary>
/// Rendering options
/// </summary>
public class RenderOptions
{
    public bool ShowToolCalls { get; set; } = false;
    public bool ShowToolResults { get; set; } = false;
    public bool ShowThoughts { get; set; } = false;
    public bool ShowCode { get; set; } = true;
    public bool ShowErrors { get; set; } = true;
    public bool ShowCommands { get; set; } = true;
    public bool HighlightFileReferences { get; set; } = true;
    public bool HighlightQuestions { get; set; } = true;
    public bool UseEmoji { get; set; } = false;
    public bool UseCodeBlockMarkers { get; set; } = true;
    public bool FormatJson { get; set; } = true;
    public string NodeSeparator { get; set; } = "\n";
    public ToolCallDisplayFormat ToolCallFormat { get; set; } = ToolCallDisplayFormat.Hidden;
    public ToolResultDisplayFormat ToolResultFormat { get; set; } = ToolResultDisplayFormat.Hidden;
}

public enum ToolCallDisplayFormat
{
    Hidden,
    Summary,
    Full
}

public enum ToolResultDisplayFormat
{
    Hidden,
    Summary,
    Full
}

/// <summary>
/// Result of rendering for streaming scenarios
/// </summary>
public class StreamingRenderResult
{
    public string TextContent { get; set; } = "";
    public List<ModelToolCall> ToolCalls { get; set; } = new();
    public bool HasContent { get; set; }
    public bool HasToolCalls { get; set; }
}