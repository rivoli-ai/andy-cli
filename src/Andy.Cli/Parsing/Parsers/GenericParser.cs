using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Andy.Cli.Services;

namespace Andy.Cli.Parsing.Parsers;

/// <summary>
/// Generic parser for models that return plain text or simple JSON tool calls
/// Used for Llama, Mistral, and other models without special formatting
/// </summary>
public class GenericParser : BaseParser
{
    // Pattern for tool calls in JSON format
    private static readonly Regex ToolCallJsonPattern = new(
        @"\{[^{}]*[""']tool[""']\s*:\s*[""']([^""']+)[""'][^{}]*\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public GenericParser(IJsonRepairService jsonRepair, ILogger? logger = null)
        : base(jsonRepair, logger)
    {
    }

    public override ResponseNode Parse(string response, ParserContext? context = null)
    {
        var root = new ResponseNode
        {
            ModelProvider = context?.ModelProvider ?? "generic",
            ModelName = context?.ModelName ?? ""
        };

        if (string.IsNullOrWhiteSpace(response))
        {
            _logger?.LogWarning("GenericParser: Received empty response");
            return root;
        }

        _logger?.LogDebug("GenericParser: Parsing response of length {Length}", response.Length);
        
        // Extract tool calls if present
        var (toolCalls, cleanedText) = ExtractToolCalls(response);
        
        _logger?.LogDebug("GenericParser: Found {ToolCount} tool calls, cleaned text length: {TextLength}", 
            toolCalls.Count, cleanedText.Length);
        
        // Add tool calls to AST
        foreach (var toolCall in toolCalls)
        {
            root.Children.Add(toolCall);
        }

        // Extract code blocks and remove them from text
        var codeBlocks = ExtractAndRemoveCodeBlocks(ref cleanedText);
        foreach (var code in codeBlocks)
        {
            root.Children.Add(code);
        }

        // Extract semantic elements (file refs, questions, etc.)
        ExtractSemanticElements(root, cleanedText, context);

        // Add remaining text as text node if not empty
        cleanedText = cleanedText.Trim();
        
        _logger?.LogDebug("GenericParser: Final cleaned text length: {Length}, preview: {Preview}", 
            cleanedText.Length, 
            cleanedText.Length > 100 ? cleanedText.Substring(0, 100) + "..." : cleanedText);
        
        if (!string.IsNullOrWhiteSpace(cleanedText))
        {
            root.Children.Add(new TextNode
            {
                Content = cleanedText,
                Format = TextFormat.Plain
            });
        }

        _logger?.LogDebug("GenericParser: Created AST with {NodeCount} child nodes", root.Children.Count);

        return root;
    }

    private (List<ToolCallNode>, string) ExtractToolCalls(string response)
    {
        var toolCalls = new List<ToolCallNode>();
        var cleanedText = response;

        // Try to find tool calls in JSON format
        var matches = ToolCallJsonPattern.Matches(response);
        foreach (Match match in matches)
        {
            try
            {
                var json = match.Value;
                var parsed = JsonDocument.Parse(json);
                var root = parsed.RootElement;

                if (root.TryGetProperty("tool", out var toolElement))
                {
                    var toolName = toolElement.GetString();
                    var parameters = new Dictionary<string, object?>();

                    if (root.TryGetProperty("parameters", out var paramsElement))
                    {
                        parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                            paramsElement.GetRawText()) ?? new Dictionary<string, object?>();
                    }

                    if (!string.IsNullOrEmpty(toolName))
                    {
                        toolCalls.Add(new ToolCallNode
                        {
                            ToolName = toolName,
                            Arguments = parameters,
                            CallId = "call_" + Guid.NewGuid().ToString("N")
                        });

                        // Remove the tool call from the text
                        cleanedText = cleanedText.Replace(match.Value, "").Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Failed to parse potential tool call: {Error}", ex.Message);
            }
        }

        return (toolCalls, cleanedText);
    }

    private List<CodeNode> ExtractAndRemoveCodeBlocks(ref string text)
    {
        var codeBlocks = new List<CodeNode>();
        var matches = CodeBlockPattern.Matches(text);

        foreach (Match match in matches.OrderByDescending(m => m.Index))
        {
            var language = match.Groups["lang"].Value;
            var code = match.Groups["code"].Value;

            codeBlocks.Insert(0, new CodeNode
            {
                Language = string.IsNullOrEmpty(language) ? "plaintext" : language,
                Code = code.Trim()
            });

            // Remove the code block from text
            text = text.Remove(match.Index, match.Length);
        }

        return codeBlocks;
    }

    public override ParserCapabilities GetCapabilities()
    {
        return new ParserCapabilities
        {
            SupportsStreaming = false,
            SupportsToolCalls = true,
            SupportsCodeBlocks = true,
            SupportsMarkdown = true,
            SupportsFileReferences = true,
            SupportsQuestions = true,
            SupportsThoughts = false,
            SupportedFormats = new List<string> { "text", "json" }
        };
    }
}