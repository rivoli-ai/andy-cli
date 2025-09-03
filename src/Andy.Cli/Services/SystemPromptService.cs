using System.Text;
using Andy.Tools.Core;

namespace Andy.Cli.Services;

/// <summary>
/// Service for building comprehensive system prompts for AI conversation
/// </summary>
public class SystemPromptService
{
    private readonly StringBuilder _prompt = new();

    public string BuildSystemPrompt(IEnumerable<ToolRegistration> availableTools)
    {
        _prompt.Clear();
        
        AddCoreMandates();
        AddEnvironmentContext();
        AddAvailableTools(availableTools);
        AddWorkflowGuidelines();
        AddToolUsageInstructions();
        AddCriticalInstructions();
        
        return _prompt.ToString();
    }

    private void AddCoreMandates()
    {
        _prompt.AppendLine("You are an AI assistant with tool access. Be helpful, accurate, and concise.");
        _prompt.AppendLine($"Environment: {Environment.OSVersion.Platform}, {Environment.CurrentDirectory}, {DateTime.Now:yyyy-MM-dd}");
        _prompt.AppendLine();
    }

    private void AddEnvironmentContext()
    {
        // Environment info is now included in core mandates for brevity
    }

    private void AddAvailableTools(IEnumerable<ToolRegistration> tools)
    {
        _prompt.AppendLine("## Available Tools");
        _prompt.AppendLine();
        
        // Condensed tool listing - one line per tool with essential info
        foreach (var tool in tools.OrderBy(t => t.Metadata.Category).ThenBy(t => t.Metadata.Name))
        {
            _prompt.Append($"- `{tool.Metadata.Id}`: {tool.Metadata.Description}");
            
            if (tool.Metadata.Parameters.Any())
            {
                var requiredParams = tool.Metadata.Parameters.Where(p => p.Required).Select(p => p.Name);
                if (requiredParams.Any())
                {
                    _prompt.Append($" (required: {string.Join(", ", requiredParams)})");
                }
            }
            
            _prompt.AppendLine();
        }
        
        _prompt.AppendLine();
    }

    private void AddWorkflowGuidelines()
    {
        _prompt.AppendLine("## How Tools Work");
        _prompt.AppendLine("When you need to use a tool:");
        _prompt.AppendLine("1. Output ONLY the JSON: {\"tool\":\"tool_id\",\"parameters\":{}}");
        _prompt.AppendLine("2. I will execute the tool and respond with [Tool Results]");
        _prompt.AppendLine("3. Then continue with your response based on the actual results");
        _prompt.AppendLine();
    }

    private void AddToolUsageInstructions()
    {
        // This section is now merged into workflow guidelines for brevity
    }

    private void AddCriticalInstructions()
    {
        _prompt.AppendLine("## CRITICAL: How to Use Tools");
        _prompt.AppendLine("To execute a tool, you MUST respond with ONLY this JSON format:");
        _prompt.AppendLine("```json");
        _prompt.AppendLine("{\"tool\":\"tool_id\",\"parameters\":{}}");
        _prompt.AppendLine("```");
        _prompt.AppendLine();
        _prompt.AppendLine("IMPORTANT:");
        _prompt.AppendLine("- NEVER write fake [Tool Results] - I will provide real results");
        _prompt.AppendLine("- NEVER pretend a tool was called - actually call it with JSON");
        _prompt.AppendLine("- Wait for my [Tool Results] response before continuing");
        _prompt.AppendLine("- For 'list files': use {\"tool\":\"list_directory\",\"parameters\":{\"path\":\".\"}}");
        _prompt.AppendLine();
    }

    private string FormatParameterType(string type)
    {
        return type.ToLower() switch
        {
            "string" => "text",
            "integer" or "int" => "number",
            "boolean" or "bool" => "true/false",
            "array" => "list",
            "object" => "object",
            _ => type
        };
    }
}