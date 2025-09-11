using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Andy.Cli.Parsing;
using Andy.Cli.Parsing.Compiler;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Andy.Llm.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.Conversation;

/// <summary>
/// Handles response compilation and rendering for AI conversations
/// </summary>
public class ResponseCompiler
{
    private readonly FeedView _feedView;
    private readonly IJsonRepairService _jsonRepair;
    private readonly ILogger? _logger;
    private LlmResponseCompiler _compiler;
    private string _modelName;
    private string _providerName;

    public ResponseCompiler(
        FeedView feedView,
        IJsonRepairService jsonRepair,
        string modelName,
        string providerName,
        ILogger? logger = null)
    {
        _feedView = feedView;
        _jsonRepair = jsonRepair;
        _modelName = modelName;
        _providerName = providerName;
        _logger = logger;
        _compiler = new LlmResponseCompiler(providerName, jsonRepair, logger as ILogger<LlmResponseCompiler>);
    }

    public void UpdateModelInfo(string modelName, string providerName)
    {
        _modelName = modelName;
        _providerName = providerName;
        // Recreate compiler with new provider
        _compiler = new LlmResponseCompiler(providerName, _jsonRepair, _logger as ILogger<LlmResponseCompiler>);
    }

    public async Task<string> CompileAndDisplayResponseAsync(LlmResponse response)
    {
        var content = response.Content ?? "";
        
        // Try to compile the response
        var compiledResult = _compiler.Compile(content);
        
        if (compiledResult?.Ast != null)
        {
            await DisplayParsedResponseAsync(compiledResult.Ast);
            
            // Extract display text from AST
            var displayText = ExtractDisplayText(compiledResult.Ast);
            if (!string.IsNullOrWhiteSpace(displayText))
            {
                return displayText;
            }
        }

        // Fallback to raw content if compilation failed or produced no output
        if (!string.IsNullOrWhiteSpace(content))
        {
            await RenderTextWithCodeExtractionAsync(content);
            return content;
        }

        return "No response content";
    }

    private async Task DisplayParsedResponseAsync(ResponseNode ast)
    {
        // Display any text nodes
        var textNodes = GetTextNodes(ast);
        foreach (var textNode in textNodes)
        {
            if (!string.IsNullOrWhiteSpace(textNode.Content))
            {
                await RenderTextWithCodeExtractionAsync(textNode.Content);
            }
        }
    }

    private List<TextNode> GetTextNodes(AstNode node)
    {
        var textNodes = new List<TextNode>();
        
        if (node is TextNode textNode)
        {
            textNodes.Add(textNode);
        }

        // Recursively get text nodes from children
        foreach (var child in node.Children)
        {
            textNodes.AddRange(GetTextNodes(child));
        }

        return textNodes;
    }

    private string ExtractDisplayText(ResponseNode node)
    {
        var sb = new StringBuilder();
        var textNodes = GetTextNodes(node);
        
        foreach (var textNode in textNodes)
        {
            if (!string.IsNullOrWhiteSpace(textNode.Content))
            {
                sb.AppendLine(textNode.Content.Trim());
            }
        }

        return sb.ToString().Trim();
    }

    private async Task RenderTextWithCodeExtractionAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Check for code blocks
        var codeBlockPattern = @"```(\w+)?\n(.*?)```";
        var matches = Regex.Matches(text, codeBlockPattern, RegexOptions.Singleline);

        if (matches.Count > 0)
        {
            var lastIndex = 0;
            foreach (Match match in matches)
            {
                // Render text before code block
                if (match.Index > lastIndex)
                {
                    var beforeText = text.Substring(lastIndex, match.Index - lastIndex);
                    await RenderMarkdownOrInlineJsonAsync(beforeText);
                }

                // Render code block
                var language = match.Groups[1].Value;
                var code = match.Groups[2].Value;
                _feedView.AddCode(code, language);

                lastIndex = match.Index + match.Length;
            }

            // Render remaining text
            if (lastIndex < text.Length)
            {
                var remainingText = text.Substring(lastIndex);
                await RenderMarkdownOrInlineJsonAsync(remainingText);
            }
        }
        else
        {
            // No code blocks, render as markdown or inline JSON
            await RenderMarkdownOrInlineJsonAsync(text);
        }

        await Task.CompletedTask;
    }

    private async Task RenderMarkdownOrInlineJsonAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Check for inline JSON
        var jsonPattern = @"\{[^{}]*\}|\[[^\[\]]*\]";
        var matches = Regex.Matches(text, jsonPattern);

        if (matches.Count > 0 && text.Trim().StartsWith("{") || text.Trim().StartsWith("["))
        {
            // Likely JSON response
            try
            {
                _jsonRepair.TryRepairJson(text, out var repaired);
                _feedView.AddCode(repaired, "json");
            }
            catch
            {
                // Not valid JSON, render as text
                _feedView.AddMarkdown(text);
            }
        }
        else
        {
            // Regular markdown text
            _feedView.AddMarkdown(text);
        }

        await Task.CompletedTask;
    }

    public string ExtractNonToolText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Remove tool call patterns
        var patterns = new[]
        {
            @"<tool_call>.*?</tool_call>",
            @"```tool_call.*?```",
            @"Tool:\s*\w+.*?(?=\n\n|\z)",
            @"Parameters:.*?(?=\n\n|\z)",
            @"Output:.*?(?=\n\n|\z)"
        };

        var result = text;
        foreach (var pattern in patterns)
        {
            result = Regex.Replace(result, pattern, "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }

        // Clean up extra whitespace
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        return result.Trim();
    }
}