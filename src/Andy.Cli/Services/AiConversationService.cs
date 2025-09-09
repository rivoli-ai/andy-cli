using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Diagnostics;
using Andy.Cli.Parsing;
using Andy.Cli.Parsing.Compiler;
using Andy.Cli.Parsing.Rendering;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Andy.Cli.Services;

/// <summary>
/// Main AI conversation service with tool integration
/// </summary>
public class AiConversationService
{
    private readonly LlmClient _llmClient;
    private readonly ToolExecutionService _toolService;
    private readonly ContextManager _contextManager;
    private readonly FeedView _feed;
    private readonly IToolRegistry _toolRegistry;
    private LlmResponseCompiler _compiler;
    private readonly AstRenderer _renderer;
    private readonly ILogger<AiConversationService>? _logger;
    private readonly QwenResponseDiagnostic? _diagnostic;
    private readonly IToolCallValidator _toolCallValidator;
    private readonly IJsonRepairService _jsonRepair;
    private string _currentModel = "";
    private string _currentProvider = "";
    // Note: Avoid writing partial streaming chunks to the feed to prevent broken/partial lines

    public AiConversationService(
        LlmClient llmClient,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        FeedView feed,
        string systemPrompt,
        IJsonRepairService jsonRepair,
        ILogger<AiConversationService>? logger = null,
        string modelName = "",
        string providerName = "")
    {
        _llmClient = llmClient;
        _toolRegistry = toolRegistry;
        _feed = feed;
        _logger = logger;
        _toolService = new ToolExecutionService(toolRegistry, toolExecutor, feed);
        _contextManager = new ContextManager(systemPrompt);
        _jsonRepair = jsonRepair;
        _compiler = new LlmResponseCompiler(NormalizeProviderName(providerName, modelName), jsonRepair, null);
        _renderer = new AstRenderer(new RenderOptions
        {
            ShowToolCalls = false,
            UseEmoji = false,
            ToolCallFormat = ToolCallDisplayFormat.Hidden
        }, null);
        _currentModel = modelName;
        _currentProvider = providerName;
        _toolCallValidator = new ToolCallValidator(_toolRegistry, logger as ILogger<ToolCallValidator>);

        // Initialize diagnostic tool if debugging is enabled
        if (Environment.GetEnvironmentVariable("ANDY_DEBUG_RAW") == "true")
        {
            _diagnostic = new QwenResponseDiagnostic(logger as ILogger<QwenResponseDiagnostic>);
            _logger?.LogInformation("Raw response diagnostic enabled");

            // Write debug info file
            var debugFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".andy",
                "diagnostics",
                "SERVICE_INIT.txt"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(debugFile)!);
            File.WriteAllText(debugFile, $"AiConversationService initialized at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\nModel: {modelName}\nProvider: {providerName}\n");
        }
    }

    /// <summary>
    /// Update the current model and provider information
    /// </summary>
    public void UpdateModelInfo(string modelName, string providerName)
    {
        _currentModel = modelName;
        _currentProvider = providerName;
        // Recreate compiler with normalized provider to ensure correct parsing (e.g., Qwen via other providers)
        _compiler = new LlmResponseCompiler(NormalizeProviderName(_currentProvider, _currentModel), _jsonRepair, null);
    }

    /// <summary>
    /// Process a user message with tool support and streaming responses
    /// </summary>
    public async Task<string> ProcessMessageAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        return await ProcessMessageAsync(userMessage, enableStreaming: false, cancellationToken);
    }

    /// <summary>
    /// Process a user message with tool support
    /// </summary>
    public async Task<string> ProcessMessageAsync(
        string userMessage,
        bool enableStreaming,
        CancellationToken cancellationToken = default)
    {
        // Add user message to context
        _contextManager.AddUserMessage(userMessage);

        // Get available tools
        var availableTools = _toolRegistry.GetTools(enabledOnly: true).ToList();

        // Create the LLM request with tool definitions
        var request = CreateRequestWithTools(availableTools);

        // Heuristic: proactively explore repository structure for repo/content questions
        if (LooksLikeRepoExploration(userMessage))
        {
            try
            {
                await PreloadRepoContextAsync(cancellationToken);
                // Refresh request after preloading tool results
                request = _contextManager.GetContext().CreateRequest();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "PreloadRepoContextAsync failed");
            }
        }

        // Track the conversation loop
        var maxIterations = 12; // Allow deeper multi-step planning and execution
        var iteration = 0;
        var finalResponse = new StringBuilder();

        while (iteration < maxIterations)
        {
            iteration++;

            // Get LLM response with streaming if enabled
            // Always render final content once; do not render partial streaming deltas to the feed
            var response = enableStreaming
                ? await GetLlmStreamingResponseAsync(request, cancellationToken)
                : await GetLlmResponseAsync(request, cancellationToken);

            if (response == null)
            {
                break;
            }

            // Parse the response using the AST compiler
            var compilerOptions = new CompilerOptions
            {
                ModelProvider = NormalizeProviderName(_currentProvider, _currentModel),
                ModelName = _currentModel,
                PreserveThoughts = false,
                EnableOptimizations = true
            };

            var compilationResult = _compiler.Compile(response.Content ?? "", compilerOptions);

            // Log any compilation diagnostics
            foreach (var diagnostic in compilationResult.Diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    _logger?.LogError("Compilation error: {Message}", diagnostic.Message);
                else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                    _logger?.LogWarning("Compilation warning: {Message}", diagnostic.Message);
            }

            if (!compilationResult.Success || compilationResult.Ast == null)
            {
                _logger?.LogWarning("Failed to parse response cleanly, falling back to basic parsing");

                // Fall back to basic parsing - still try to extract tool calls
                compilationResult.Ast = new ResponseNode
                {
                    ModelProvider = _currentProvider,
                    ModelName = _currentModel
                };

                // Add the raw text as a text node (will be cleaned by renderer)
                compilationResult.Ast.Children.Add(new TextNode
                {
                    Content = response.Content ?? "",
                    Format = TextFormat.Plain
                });
            }

            // Render the AST for display and tool extraction
            var renderResult = _renderer.RenderForStreaming(compilationResult.Ast);

            if (renderResult.ToolCalls.Any())
            {
                // Display text content if it exists
                if (!string.IsNullOrWhiteSpace(renderResult.TextContent))
                {
                    var text = SanitizeAssistantText(renderResult.TextContent);
                    // Filter "Please wait" boilerplate
                    if (!text.TrimStart().StartsWith("Please wait", StringComparison.OrdinalIgnoreCase))
                    {
                        _feed.AddMarkdownRich(text);
                    }
                }

                // Generate call IDs for each tool call and add them as assistant tool_calls
                var contextToolCalls = new List<ContextToolCall>();
                var callIdMap = new Dictionary<int, string>();
                for (int i = 0; i < renderResult.ToolCalls.Count; i++)
                {
                    var generatedCallId = "call_" + Guid.NewGuid().ToString("N");
                    callIdMap[i] = generatedCallId;
                    contextToolCalls.Add(new ContextToolCall
                    {
                        CallId = generatedCallId,
                        ToolId = renderResult.ToolCalls[i].ToolId,
                        Parameters = renderResult.ToolCalls[i].Parameters
                    });
                }

                // Add assistant message with tool calls to context
                // This is critical - the assistant message must include tool_calls
                _contextManager.AddAssistantMessage(renderResult.TextContent ?? "", contextToolCalls);

                // Execute tools and collect results
                var toolResults = new List<string>();

                for (int i = 0; i < renderResult.ToolCalls.Count; i++)
                {
                    var toolCall = renderResult.ToolCalls[i];
                    var thisCallId = callIdMap[i];

                    // Validate and optionally repair parameters before execution
                    var toolMetadata = _toolRegistry.GetTool(toolCall.ToolId)?.Metadata;
                    var callToExecute = toolCall;
                    if (toolMetadata != null)
                    {
                        var validation = _toolCallValidator.Validate(toolCall, toolMetadata);
                        if (!validation.IsValid && validation.RepairedCall != null)
                        {
                            _logger?.LogWarning("Parameters for tool '{Tool}' were invalid; applying single repair attempt.", toolCall.ToolId);
                            callToExecute = validation.RepairedCall;
                        }
                    }

                    // Execute tool
                    var execResult = await _toolService.ExecuteToolAsync(
                        callToExecute.ToolId,
                        callToExecute.Parameters,
                        cancellationToken);

                    // Prepare output string (prefer JSON Data)
                    string outputStr;
                    if (execResult.Data != null)
                    {
                        try
                        {
                            outputStr = SerializeToolDataWithTruncation(execResult.Data, callToExecute.ToolId, 2000);
                        }
                        catch
                        {
                            outputStr = execResult.Data.ToString() ?? execResult.Message ?? execResult.ErrorMessage ?? "No output";
                        }
                    }
                    else if (!string.IsNullOrEmpty(execResult.FullOutput))
                    {
                        outputStr = execResult.FullOutput!;
                    }
                    else
                    {
                        outputStr = execResult.Message ?? execResult.ErrorMessage ?? "No output";
                    }

                    // Add tool response to context referencing the correct call_id
                    _contextManager.AddToolExecution(
                        callToExecute.ToolId,
                        thisCallId,
                        callToExecute.Parameters,
                        outputStr);

                    toolResults.Add(outputStr);
                }

                // Don't add tool results as user message - they're already in context as tool responses

                // Update request for next iteration
                request = _contextManager.GetContext().CreateRequest();
            }
            else
            {
                // Regular text response - rendered content without tool calls
                var content = SanitizeAssistantText(renderResult.TextContent ?? "");

                // Check if the response looks like a raw tool execution result (escaped JSON)
                if (content.StartsWith("\"\\\"[Tool Execution:") || content.StartsWith("\"[Tool Execution:"))
                {
                    // Try to unescape and parse the content
                    try
                    {
                        // Remove outer quotes if present
                        if (content.StartsWith("\"") && content.EndsWith("\""))
                        {
                            content = content.Substring(1, content.Length - 2);
                        }

                        // Unescape the content
                        content = content.Replace("\\n", "\n")
                                       .Replace("\\\"", "\"")
                                       .Replace("\\\\", "\\")
                                       .Replace("\\u0022", "\"");
                    }
                    catch
                    {
                        // If unescaping fails, use original content
                    }

                    finalResponse.AppendLine(content);
                    _contextManager.AddAssistantMessage(content);

                    // Display the response with proper formatting
                    DisplayParsedResponse(compilationResult.Ast, renderResult);
                }
                else
                {
                    // Fallback: try QwenResponseParser-based extraction if AST produced no tool calls
                    var fallbackParser = new QwenResponseParser(
                        _jsonRepair,
                        new StreamingToolCallAccumulator(_jsonRepair, null),
                        null);
                    var fallbackCalls = fallbackParser.ExtractToolCalls(response.Content ?? string.Empty);
                    if (fallbackCalls.Any())
                    {
                        // Execute fallback tool calls path (mirrors main tool call execution flow)
                        var contextToolCalls = new List<ContextToolCall>();
                        var callIdMap = new Dictionary<int, string>();
                        for (int i = 0; i < fallbackCalls.Count; i++)
                        {
                            var generatedCallId = "call_" + Guid.NewGuid().ToString("N");
                            callIdMap[i] = generatedCallId;
                            contextToolCalls.Add(new ContextToolCall
                            {
                                CallId = generatedCallId,
                                ToolId = fallbackCalls[i].ToolId,
                                Parameters = fallbackCalls[i].Parameters
                            });
                        }

                        _contextManager.AddAssistantMessage(renderResult.TextContent ?? string.Empty, contextToolCalls);

                        for (int i = 0; i < fallbackCalls.Count; i++)
                        {
                            var toolCall = fallbackCalls[i];
                            var thisCallId = callIdMap[i];

                            var toolMetadata = _toolRegistry.GetTool(toolCall.ToolId)?.Metadata;
                            var callToExecute = toolCall;
                            if (toolMetadata != null)
                            {
                                var validation = _toolCallValidator.Validate(toolCall, toolMetadata);
                                if (!validation.IsValid && validation.RepairedCall != null)
                                {
                                    _logger?.LogWarning("Parameters for tool '{Tool}' were invalid; applying single repair attempt.", toolCall.ToolId);
                                    callToExecute = validation.RepairedCall;
                                }
                            }

                            // Use raw execution to preserve original parameter names from model output
                            var execResult = await _toolService.ExecuteRawAsync(
                                callToExecute.ToolId,
                                callToExecute.Parameters,
                                cancellationToken);

                            string outputStr;
                            if (execResult.Data != null)
                            {
                                try
                                {
                                    outputStr = SerializeToolDataWithTruncation(execResult.Data, callToExecute.ToolId, 2000);
                                }
                                catch
                                {
                                    outputStr = execResult.Data.ToString() ?? execResult.Message ?? execResult.ErrorMessage ?? "No output";
                                }
                            }
                            else if (!string.IsNullOrEmpty(execResult.FullOutput))
                            {
                                outputStr = execResult.FullOutput!;
                            }
                            else
                            {
                                outputStr = execResult.Message ?? execResult.ErrorMessage ?? "No output";
                            }

                            _contextManager.AddToolExecution(
                                callToExecute.ToolId,
                                thisCallId,
                                callToExecute.Parameters,
                                outputStr);
                        }

                        // We executed fallback tool calls; stop loop like normal text path
                        request = _contextManager.GetContext().CreateRequest();
                        break;
                    }

                    finalResponse.AppendLine(content);
                    _contextManager.AddAssistantMessage(content);

                    // Display the response with proper formatting
                    DisplayParsedResponse(compilationResult.Ast, renderResult);
                }

                break; // No more tool calls, we're done
            }
        }

        // Show context stats
        var stats = _contextManager.GetStats();
        _feed.AddMarkdownRich($"*Context: {stats.MessageCount} messages, ~{stats.EstimatedTokens} tokens, {stats.ToolCallCount} tool calls*");

        return finalResponse.ToString();
    }

    private bool LooksLikeRepoExploration(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return false;
        var s = userMessage.ToLowerInvariant();
        return (s.Contains("what's in") || s.Contains("what is in") || s.Contains("contents") || s.Contains("list") || s.Contains("show files") || s.Contains("source code") || s.Contains("repo"))
               && (s.Contains("src") || s.Contains("repo") || s.Contains("code"));
    }

    private async Task PreloadRepoContextAsync(CancellationToken cancellationToken)
    {
        // Build a batch of assistant-declared tool_calls (client-initiated), then execute them
        var initialCalls = new List<ModelToolCall>();

        // Top-level and src structure
        initialCalls.Add(new ModelToolCall { ToolId = "list_directory", Parameters = new Dictionary<string, object?> { ["path"] = "." } });
        initialCalls.Add(new ModelToolCall { ToolId = "list_directory", Parameters = new Dictionary<string, object?> { ["path"] = "./src" } });
        initialCalls.Add(new ModelToolCall { ToolId = "list_directory", Parameters = new Dictionary<string, object?> { ["path"] = "./src/Andy.Cli" } });

        // Key files commonly useful for summary
        var candidateFiles = new[]
        {
            "README.md",
            "./src/Andy.Cli/Andy.Cli.csproj",
            "./src/Andy.Cli/Program.cs"
        };
        foreach (var f in candidateFiles)
        {
            initialCalls.Add(new ModelToolCall { ToolId = "read_file", Parameters = new Dictionary<string, object?> { ["file_path"] = f } });
        }

        // Post a synthetic assistant message with tool_calls
        var contextToolCalls = new List<ContextToolCall>();
        var callIdMap = new List<(string ToolId, string CallId, Dictionary<string, object?> Params)>();
        foreach (var call in initialCalls)
        {
            var id = "call_" + Guid.NewGuid().ToString("N");
            contextToolCalls.Add(new ContextToolCall { CallId = id, ToolId = call.ToolId, Parameters = call.Parameters });
            callIdMap.Add((call.ToolId, id, call.Parameters));
        }
        _contextManager.AddAssistantMessage("", contextToolCalls);

        // Execute each tool call and add tool responses to context
        foreach (var call in callIdMap)
        {
            try
            {
                var execResult = await _toolService.ExecuteToolAsync(call.ToolId, call.Params, cancellationToken);
                string outputStr;
                if (execResult.Data != null)
                {
                    try
                    {
                        outputStr = SerializeToolDataWithTruncation(execResult.Data, call.ToolId, 2000);
                    }
                    catch
                    {
                        outputStr = execResult.Data.ToString() ?? execResult.Message ?? execResult.ErrorMessage ?? "No output";
                    }
                }
                else if (!string.IsNullOrEmpty(execResult.FullOutput))
                {
                    outputStr = execResult.FullOutput!;
                }
                else
                {
                    outputStr = execResult.Message ?? execResult.ErrorMessage ?? "No output";
                }

                _contextManager.AddToolExecution(call.ToolId, call.CallId, call.Params, outputStr);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Preload tool call failed: {Tool}", call.ToolId);
            }
        }
    }

    /// <summary>
    /// Create LLM request with tool definitions
    /// </summary>
    private LlmRequest CreateRequestWithTools(List<ToolRegistration> tools)
    {
        var context = _contextManager.GetContext();
        var request = context.CreateRequest();

        // Tool definitions are already in the system prompt from SystemPromptService
        // Don't add them again to avoid duplication

        return request;
    }

    // Removed BuildToolPrompt - tool definitions are in system prompt

    /// <summary>
    /// Get LLM response with streaming support
    /// </summary>
    private async Task<LlmResponse?> GetLlmResponseAsync(
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _llmClient.CompleteAsync(request, cancellationToken);

            // Capture raw response for diagnostics
            if (_diagnostic != null && response != null)
            {
                // Log the full request first
                var requestJson = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await _diagnostic.CaptureRawRequest(requestJson);

                // Then capture the response
                var lastUserMessage = request.Messages.LastOrDefault(m => m.Role.ToString() == "User");
                var prompt = lastUserMessage?.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "";
                await _diagnostic.CaptureRawResponse(prompt, response.Content ?? "");
            }

            // Raw output is captured in diagnostic files when ANDY_DEBUG_RAW=true

            return response;
        }
        catch (Exception ex)
        {
            _feed.AddMarkdownRich($"**LLM Error:** {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get LLM response with streaming enabled
    /// </summary>
    private async Task<LlmResponse?> GetLlmStreamingResponseAsync(
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullContent = new StringBuilder();
            var isComplete = false;
            string? finishReason = null;
            var functionCalls = new List<FunctionCall>();

            // Accumulate the entire response during streaming
            // For Qwen models, tool calls are only valid when streaming is complete
            await foreach (var chunk in _llmClient.StreamCompleteAsync(request, cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    fullContent.Append(chunk.TextDelta);
                }

                // Accumulate function calls
                if (chunk.FunctionCall != null)
                {
                    functionCalls.Add(chunk.FunctionCall);
                }

                // Check if streaming is complete
                if (chunk.IsComplete)
                {
                    isComplete = true;
                    finishReason = "stop";  // Set a default finish reason
                }
            }

            var content = fullContent.ToString();

            // Capture raw response for diagnostics
            if (_diagnostic != null)
            {
                // Log the full request first
                var requestJson = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await _diagnostic.CaptureRawRequest(requestJson);

                // Then capture the response
                var lastUserMessage = request.Messages.LastOrDefault(m => m.Role.ToString() == "User");
                var prompt = lastUserMessage?.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "";
                await _diagnostic.CaptureRawResponse(prompt, content);
            }

            // Raw output is captured in diagnostic files when ANDY_DEBUG_RAW=true

            // Only process tool calls if streaming is complete; otherwise, return what we have
            if (!isComplete)
            {
                return new LlmResponse
                {
                    Content = fullContent.ToString(),
                    FinishReason = finishReason
                };
            }

            // Return the raw content for processing in the main loop
            return new LlmResponse
            {
                Content = content,
                FinishReason = finishReason
            };
        }
        catch (Exception ex)
        {
            _feed.AddMarkdownRich($"**LLM Error:** {ex.Message}");
            return null;
        }
    }

    // Tool call extraction now handled by AST compiler

    // Tool result formatting and text extraction now handled by AST compiler

    /// <summary>
    /// Display parsed response with proper formatting for code blocks
    /// </summary>
    private void DisplayParsedResponse(ResponseNode? ast, StreamingRenderResult renderResult)
    {
        if (ast == null)
        {
            // Fallback to simple markdown display
            if (!string.IsNullOrEmpty(renderResult.TextContent))
            {
                _feed.AddMarkdownRich(renderResult.TextContent);
            }
            return;
        }

        // Walk through AST nodes and display them appropriately
        var hasDisplayedContent = false;
        var textBuffer = new System.Text.StringBuilder();

        foreach (var node in ast.Children)
        {
            switch (node)
            {
                case CodeNode codeNode:
                    // Flush any buffered text first
                    if (textBuffer.Length > 0)
                    {
                        RenderTextWithCodeExtraction(SanitizeAssistantText(textBuffer.ToString()));
                        textBuffer.Clear();
                        hasDisplayedContent = true;
                    }
                    // Display code block with syntax highlighting
                    _feed.AddCode(codeNode.Code, codeNode.Language);
                    hasDisplayedContent = true;
                    break;

                case TextNode textNode:
                    // Buffer text to combine adjacent text nodes
                    textBuffer.AppendLine(textNode.Content);
                    break;

                case ErrorNode errorNode when errorNode.Severity == ErrorSeverity.Warning:
                    // Display warnings
                    if (textBuffer.Length > 0)
                    {
                        _feed.AddMarkdownRich(SanitizeAssistantText(textBuffer.ToString()));
                        textBuffer.Clear();
                    }
                    _feed.AddMarkdownRich($"**⚠️ {errorNode.Message}**");
                    hasDisplayedContent = true;
                    break;

                case FileReferenceNode fileRef:
                    // Include file references in text
                    textBuffer.Append(fileRef.Path);
                    if (!string.IsNullOrEmpty(fileRef.LineReference))
                    {
                        textBuffer.Append(fileRef.LineReference);
                    }
                    break;

                case QuestionNode question:
                    // Include questions in text
                    textBuffer.AppendLine(question.Question);
                    break;

                case CommandNode command:
                    // Flush text and display command
                    if (textBuffer.Length > 0)
                    {
                        _feed.AddMarkdownRich(SanitizeAssistantText(textBuffer.ToString()));
                        textBuffer.Clear();
                        hasDisplayedContent = true;
                    }
                    _feed.AddCode(command.Command, "bash");
                    hasDisplayedContent = true;
                    break;

                // Skip tool calls and thoughts - they're handled elsewhere
                case ToolCallNode:
                case ThoughtNode:
                    break;
            }
        }

        // Flush any remaining text
        if (textBuffer.Length > 0)
        {
            RenderTextWithCodeExtraction(SanitizeAssistantText(textBuffer.ToString().Trim()));
            hasDisplayedContent = true;
        }

        // If nothing was displayed, fall back to the rendered text content
        if (!hasDisplayedContent && !string.IsNullOrEmpty(renderResult.TextContent))
        {
            RenderTextWithCodeExtraction(SanitizeAssistantText(renderResult.TextContent));
        }
    }

    /// <summary>
    /// Render text content while extracting code blocks from both proper and malformed code fences.
    /// Supports triple-backtick fences and a common malformed variant: a single-backtick followed by a language token (`csharp ... `).
    /// </summary>
    private void RenderTextWithCodeExtraction(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        int i = 0;
        while (i < normalized.Length)
        {
            int fenceStart = normalized.IndexOf("```", i, StringComparison.Ordinal);
            int singleStart = normalized.IndexOf('\n', i) >= 0 ? normalized.IndexOf('\n', i) : int.MaxValue; // anchor for scanning

            // Check for malformed single-backtick language marker at line start
            int lineStart = i;
            // Move to start of current line
            int prevNl = normalized.LastIndexOf('\n', Math.Max(0, i - 1));
            if (prevNl >= 0) lineStart = prevNl + 1;
            bool hasSingleLang = false;
            int singleMarkerIdx = lineStart;
            string? singleLang = null;
            if (lineStart < normalized.Length && normalized[lineStart] == '`' && !(lineStart + 2 < normalized.Length && normalized.Substring(lineStart, 3) == "```"))
            {
                // Collect language token after single backtick
                int langEnd = normalized.IndexOf('\n', lineStart + 1);
                if (langEnd < 0) langEnd = normalized.Length;
                var langToken = normalized.Substring(lineStart + 1, langEnd - (lineStart + 1)).Trim();
                if (!string.IsNullOrEmpty(langToken) && langToken.All(c => char.IsLetter(c) || c == '#'))
                {
                    hasSingleLang = true;
                    singleLang = langToken.ToLowerInvariant();
                    singleMarkerIdx = lineStart;
                }
            }

            // If a proper triple fence appears earlier than the current line's single marker, handle that first
            if (fenceStart >= 0 && (!hasSingleLang || fenceStart < singleMarkerIdx))
            {
                // Emit markdown preceding the code block
                if (fenceStart > i)
                {
                    var md = normalized.Substring(i, fenceStart - i);
                    if (!string.IsNullOrWhiteSpace(md)) RenderMarkdownOrInlineJson(md);
                }
                int langEnd = normalized.IndexOf('\n', fenceStart + 3);
                string? lang = null;
                int contentStart;
                if (langEnd > fenceStart + 3)
                {
                    lang = normalized.Substring(fenceStart + 3, langEnd - (fenceStart + 3)).Trim();
                    contentStart = langEnd + 1;
                }
                else
                {
                    contentStart = fenceStart + 3;
                }
                int fenceEnd = normalized.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (fenceEnd < 0) fenceEnd = normalized.Length;
                var code = normalized.Substring(contentStart, fenceEnd - contentStart);
                _feed.AddCode(code, lang);
                i = Math.Min(normalized.Length, fenceEnd + 3);
                continue;
            }

            if (hasSingleLang)
            {
                // Emit markdown before the single marker
                if (singleMarkerIdx > i)
                {
                    var md = normalized.Substring(i, singleMarkerIdx - i);
                    if (!string.IsNullOrWhiteSpace(md)) RenderMarkdownOrInlineJson(md);
                }
                // Find end marker: a line that is exactly "`" or end of text
                int afterMarker = normalized.IndexOf('\n', singleMarkerIdx);
                if (afterMarker < 0) afterMarker = singleMarkerIdx + 1;
                int scanPos = afterMarker + 1;
                int endIdx = normalized.IndexOf('\n', scanPos);
                int codeEnd = normalized.Length;
                while (endIdx >= 0)
                {
                    // Check if the line is a lone backtick
                    int lineBegin = endIdx + 1;
                    // Determine current line content
                    int nextNl = normalized.IndexOf('\n', lineBegin);
                    string line = nextNl >= 0 ? normalized.Substring(lineBegin, nextNl - lineBegin) : normalized.Substring(lineBegin);
                    if (line.Trim() == "`")
                    {
                        codeEnd = lineBegin - 1; // end just before this line
                        // Advance past closing line
                        i = nextNl >= 0 ? nextNl + 1 : normalized.Length;
                        break;
                    }
                    endIdx = nextNl;
                }
                if (endIdx < 0)
                {
                    // No explicit closing, take rest as code
                    i = normalized.Length;
                }
                var codeStart = afterMarker + 1;
                if (codeStart < 0 || codeStart > normalized.Length) codeStart = afterMarker;
                var codeText = normalized.Substring(codeStart, Math.Max(0, codeEnd - codeStart));
                _feed.AddCode(codeText, singleLang);
                continue;
            }

            // No more code blocks found; emit remaining as markdown
            var tail = normalized.Substring(i);
            if (!string.IsNullOrWhiteSpace(tail)) RenderMarkdownOrInlineJson(tail);
            break;
        }
    }

    private void RenderMarkdownOrInlineJson(string text)
    {
        // Split on lines; render any single-line valid JSON object as a code block with json language
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("{") && t.EndsWith("}"))
            {
                try
                {
                    using var _ = System.Text.Json.JsonDocument.Parse(t);
                    _feed.AddCode(t, "json");
                    continue;
                }
                catch
                {
                    // fall through to markdown
                }
            }
            if (!string.IsNullOrEmpty(line))
                _feed.AddMarkdownRich(line);
            else
                _feed.AddMarkdownRich("");
        }
    }

    /// <summary>
    /// Update the system prompt
    /// </summary>
    public void UpdateSystemPrompt(string prompt)
    {
        _contextManager.UpdateSystemPrompt(prompt);
    }

    /// <summary>
    /// Set the current model and provider for response interpretation
    /// </summary>
    public void SetModelInfo(string modelName, string providerName)
    {
        _currentModel = modelName ?? "";
        _currentProvider = providerName ?? "";
        _compiler = new LlmResponseCompiler(NormalizeProviderName(_currentProvider, _currentModel), _jsonRepair, null);
    }

    private string NormalizeProviderName(string providerName, string modelName)
    {
        var pn = providerName?.ToLowerInvariant() ?? "";
        var mn = modelName?.ToLowerInvariant() ?? "";
        // If model name indicates qwen, force provider to qwen for parsing behavior
        if (mn.Contains("qwen")) return "qwen";
        if (pn.Contains("openai")) return "openai";
        if (pn.Contains("anthropic")) return "anthropic";
        if (pn.Contains("google") || pn.Contains("gemini")) return "gemini";
        if (pn.Contains("mistral")) return "mistral";
        if (pn.Contains("meta") || mn.Contains("llama") || mn.Contains("lama")) return "llama";
        return pn;
    }

    /// <summary>
    /// Serialize tool Data with truncation for large payloads (notably read_file content).
    /// Caps long string fields like "content" and adds a summary to avoid provider 400s.
    /// </summary>
    private static string SerializeToolDataWithTruncation(object data, string toolId, int maxFieldChars)
    {
        // Convert to JsonObject for inspection
        var json = System.Text.Json.JsonSerializer.SerializeToNode(data, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        if (json is JsonObject obj)
        {
            // Special handling: read_file often returns a huge "content" string
            if (toolId == "read_file" && obj.TryGetPropertyValue("content", out var contentNode) && contentNode is JsonValue)
            {
                var content = contentNode!.GetValue<string?>();
                if (!string.IsNullOrEmpty(content) && content!.Length > maxFieldChars)
                {
                    var head = content.Substring(0, maxFieldChars);
                    var remaining = content.Length - head.Length;
                    obj["content"] = head + $"\n...[truncated {remaining} chars]";
                    obj["length"] = content.Length;
                }
            }

            // Generic cap: if any string property exceeds maxFieldChars, cap it
            foreach (var kv in obj.ToList())
            {
                if (kv.Value is JsonValue val && val.TryGetValue<string>(out var s) && s != null && s.Length > maxFieldChars)
                {
                    obj[kv.Key] = s.Substring(0, maxFieldChars) + $"\n...[truncated {s.Length - maxFieldChars} chars]";
                }
            }
        }

        return json?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }) ??
               System.Text.Json.JsonSerializer.Serialize(data);
    }

    // Removed raw execution shim; using ExecuteToolAsync with ParameterMapper ensures compatibility across tools

    /// <summary>
    /// Clear the conversation context
    /// </summary>
    public void ClearContext()
    {
        _contextManager.Clear();
    }

    /// <summary>
    /// Generate diagnostic summary if available
    /// </summary>
    public async Task GenerateDiagnosticSummaryAsync()
    {
        if (_diagnostic != null)
        {
            await _diagnostic.GenerateSummaryReport();
        }
    }

    /// <summary>
    /// Get context statistics
    /// </summary>
    public ContextStats GetContextStats()
    {
        return _contextManager.GetStats();
    }

    /// <summary>
    /// Sanitizes assistant text for display by handling escaping, unwanted characters, and hiding internal tool mentions
    /// </summary>
    private string SanitizeAssistantText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sanitized = text;

        // Remove outer quotes if the entire content is quoted
        if (sanitized.StartsWith("\"") && sanitized.EndsWith("\"") && sanitized.Length > 1)
        {
            sanitized = sanitized.Substring(1, sanitized.Length - 2);
        }

        // Unescape common escape sequences
        sanitized = sanitized.Replace("\\n", "\n")
                           .Replace("\\r", "\r")
                           .Replace("\\t", "\t")
                           .Replace("\\\"", "\"")
                           .Replace("\\'", "'")
                           .Replace("\\\\", "\\")
                           .Replace("\\u0022", "\"");

        // Hide internal tool mentions - remove lines that are just tool references
        var lines = sanitized.Split('\n');
        var filteredLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Skip lines that look like internal tool mentions
            if (ShouldHideToolMention(trimmedLine))
                continue;

            filteredLines.Add(line);
        }

        sanitized = string.Join('\n', filteredLines);

        // Trim excessive whitespace
        sanitized = sanitized.Trim();

        return sanitized;
    }

    /// <summary>
    /// Determines if a line contains an internal tool mention that should be hidden
    /// </summary>
    private bool ShouldHideToolMention(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        // Common patterns for internal tool mentions
        var toolMentionPatterns = new[]
        {
            // Pattern: [Calling toolname] or [Using toolname] etc.
            @"^\[(?:Calling|Using|Executing)\s+\w+\]$",
            
            // Pattern: I'll use the [toolname] tool
            @"^I'll use the \w+ tool",
            @"^I'm using the \w+ tool",
            @"^Let me use the \w+ tool",
            
            // Pattern: <function_calls> or similar
            @"^<[^>]*function[^>]*>",
            @"^<[^>]*invoke[^>]*>",
            
            // Pattern: Tool call: toolname
            @"^Tool call:\s*\w+",
            
            // Pattern: Invoking toolname...
            @"^Invoking\s+\w+",
            
            // Pattern: Using toolname to...
            @"^Using\s+\w+\s+to\s+",
            
            // Pattern: I need to use/call...
            @"^I need to (?:use|call)\s+",
            
            // Pattern: Let me call/invoke...  
            @"^Let me (?:call|invoke)\s+"
        };

        foreach (var pattern in toolMentionPatterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(line, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Internal tool call representation
    /// </summary>
    private class ToolCall
    {
        public string ToolId { get; set; } = "";
        public Dictionary<string, object?> Parameters { get; set; } = new();
    }
}