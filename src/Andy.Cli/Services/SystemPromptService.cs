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
        _prompt.AppendLine("## Tool Usage");
        _prompt.AppendLine("Invoke tools with JSON: `{\"tool\":\"tool_id\",\"parameters\":{\"param\":\"value\"}}`");
        _prompt.AppendLine("- Use `list_directory` to list files, `read_file` to read, `write_file` to save");
        _prompt.AppendLine("- Display code/text inline unless explicitly asked to save to file");
        _prompt.AppendLine("- Chain multiple tools when needed for complex tasks");
        _prompt.AppendLine();
    }

    private void AddToolUsageInstructions()
    {
        // This section is now merged into workflow guidelines for brevity
    }

    private void AddCriticalInstructions()
    {
        _prompt.AppendLine("## Critical Rules");
        _prompt.AppendLine("1. Return ONLY JSON when invoking tools: `{\"tool\":\"id\",\"parameters\":{...}}`");
        _prompt.AppendLine("2. NEVER claim success until you receive [Tool Results]");
        _prompt.AppendLine("3. Base responses on ACTUAL tool results, not assumptions");
        _prompt.AppendLine("4. Display code/text directly unless explicitly asked to save to file");
        _prompt.AppendLine("5. Chain tool calls when needed to complete complex tasks");
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