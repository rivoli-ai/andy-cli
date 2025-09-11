using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.Conversation;

/// <summary>
/// Handles tool execution and response processing for AI conversations
/// </summary>
public class ToolHandler
{
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly ConversationContext _context;
    private readonly FeedView _feedView;
    private readonly ILogger? _logger;
    private readonly CumulativeOutputTracker _outputTracker = new();

    public ToolHandler(
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ConversationContext context,
        FeedView feedView,
        ILogger? logger = null)
    {
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _context = context;
        _feedView = feedView;
        _logger = logger;
    }

    public async Task<List<Message>> ExecuteToolCallsAsync(
        List<FunctionCall> functionCalls,
        CancellationToken cancellationToken)
    {
        var toolResults = new List<Message>();

        foreach (var call in functionCalls)
        {
            _logger?.LogDebug("Executing tool: {Tool} with params: {Params}",
                call.Name,
                JsonSerializer.Serialize(call.Arguments));

            try
            {
                var result = await ExecuteToolAsync(call, cancellationToken);
                if (result != null)
                {
                    toolResults.Add(result);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing tool {Tool}", call.Name);
                
                var errorResult = new Message
                {
                    Role = Andy.Llm.Models.MessageRole.Tool,
                    Parts = new List<MessagePart>
                    {
                        new ToolResponsePart
                        {
                            ToolName = call.Name,
                            CallId = call.Id,
                            Response = $"Error executing tool: {ex.Message}"
                        }
                    }
                };
                toolResults.Add(errorResult);
            }
        }

        return toolResults;
    }

    private async Task<Message?> ExecuteToolAsync(
        FunctionCall call,
        CancellationToken cancellationToken)
    {
        var tool = _toolRegistry.GetTool(call.Name);
        if (tool == null)
        {
            _logger?.LogWarning("Tool not found: {Tool}", call.Name);
            return new Message
            {
                Role = Andy.Llm.Models.MessageRole.Tool,
                Parts = new List<MessagePart>
                {
                    new ToolResponsePart
                    {
                        ToolName = call.Name,
                        CallId = call.Id,
                        Response = $"Tool not found: {call.Name}"
                    }
                }
            };
        }

        // Execute the tool using the simpler interface
        var result = await _toolExecutor.ExecuteAsync(
            tool.Metadata.Id,
            call.Arguments,
            new ToolExecutionContext
            {
                CorrelationId = call.Id,
                CancellationToken = cancellationToken
            });

        // Track cumulative output
        if (result?.Output != null)
        {
            var outputStr = result.Data.ToString() ?? string.Empty;
            _outputTracker.RecordOutput(call.Name, outputStr.Length);
        }

        // Display tool output
        await DisplayToolOutputAsync(call, result, tool);

        // Create tool result message
        return CreateToolResultMessage(call, result);
    }

    private async Task DisplayToolOutputAsync(
        FunctionCall call,
        Andy.Tools.Core.ToolExecutionResult? result,
        ToolRegistration tool)
    {
        if (result == null) return;

        // Display tool name
        var toolTitle = $"Tool: {call.Name}";
        _feedView.AddMarkdown(toolTitle);

        // Display parameters if any
        if (call.Arguments?.Any() == true)
        {
            var paramsText = "Parameters: " + JsonSerializer.Serialize(call.Arguments);
            _feedView.AddMarkdown(paramsText);
        }

        // Display output
        if (result.Data != null)
        {
            var outputText = SerializeToolDataWithTruncation(result.Data, tool.Metadata.Id);
            _feedView.AddMarkdown($"Output: {outputText}");
        }

        if (!result.IsSuccessful && !string.IsNullOrEmpty(result.ErrorMessage))
        {
            _feedView.AddMarkdown($"Error: {result.ErrorMessage}");
        }

        await Task.CompletedTask;
    }

    private Message CreateToolResultMessage(FunctionCall call, Andy.Tools.Core.ToolExecutionResult? result)
    {
        if (result == null)
        {
            return new Message
            {
                Role = Andy.Llm.Models.MessageRole.Tool,
                Parts = new List<MessagePart>
                {
                    new ToolResponsePart
                    {
                        ToolName = call.Name,
                        CallId = call.Id,
                        Response = "Tool execution failed"
                    }
                }
            };
        }

        var outputString = result.Data switch
        {
            string s => s,
            _ => JsonSerializer.Serialize(result.Data)
        };

        return new Message
        {
            Role = Andy.Llm.Models.MessageRole.Tool,
            Parts = new List<MessagePart>
            {
                new ToolResponsePart
                {
                    ToolName = call.Name,
                    CallId = call.Id,
                    Response = result.IsSuccessful ? outputString : (result.ErrorMessage ?? "No output")
                }
            }
        };
    }

    private static string SerializeToolDataWithTruncation(object data, string toolId, int maxFieldChars = 5000)
    {
        try
        {
            if (data is string str)
            {
                return str.Length > maxFieldChars 
                    ? str.Substring(0, maxFieldChars) + $"... [truncated {str.Length - maxFieldChars} chars]"
                    : str;
            }

            var json = JsonSerializer.Serialize(data);
            return json.Length > maxFieldChars 
                ? json.Substring(0, maxFieldChars) + $"... [truncated {json.Length - maxFieldChars} chars]"
                : json;
        }
        catch (Exception ex)
        {
            return $"[Error serializing output: {ex.Message}]";
        }
    }

    public List<ToolDeclaration> GetToolDeclarations(List<ToolRegistration> tools)
    {
        return tools.Select(tool => new ToolDeclaration
        {
            Name = tool.Metadata.Id,
            Description = tool.Metadata.Description,
            Parameters = ConvertParametersToSchema(tool.Metadata.Parameters),
            Required = false // ToolCapability doesn't have a Required flag in this codebase
        }).ToList();
    }

    private Dictionary<string, object> ConvertParametersToSchema(IList<ToolParameter> parameters)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var param in parameters)
        {
            var propSchema = new Dictionary<string, object>
            {
                ["type"] = ConvertTypeToJsonSchema(param.Type),
                ["description"] = param.Description
            };

            if (param.DefaultValue != null)
            {
                propSchema["default"] = param.DefaultValue;
            }

            if (param.AllowedValues != null)
            {
                propSchema["enum"] = param.AllowedValues;
            }

            properties[param.Name] = propSchema;

            if (param.Required)
            {
                required.Add(param.Name);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Any())
        {
            schema["required"] = required;
        }

        return schema;
    }

    private string ConvertTypeToJsonSchema(string dotNetType)
    {
        return dotNetType.ToLower() switch
        {
            "string" => "string",
            "int" or "int32" or "int64" or "long" => "integer",
            "float" or "double" or "decimal" => "number",
            "bool" or "boolean" => "boolean",
            "array" or "list" => "array",
            "object" or "dictionary" => "object",
            _ => "string"
        };
    }

    public CumulativeOutputTracker GetOutputTracker() => _outputTracker;
}