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
        _prompt.AppendLine("You are an AI assistant with access to tools that can help you complete tasks.");
        _prompt.AppendLine();
        _prompt.AppendLine("## Core Mandates");
        _prompt.AppendLine("1. Be helpful, accurate, and concise in your responses");
        _prompt.AppendLine("2. Use available tools when they can help complete the user's request");
        _prompt.AppendLine("3. Ask for clarification when the request is ambiguous");
        _prompt.AppendLine("4. Respect user privacy and security - never expose sensitive information");
        _prompt.AppendLine();
    }

    private void AddEnvironmentContext()
    {
        _prompt.AppendLine("## Environment Context");
        _prompt.AppendLine($"- Platform: {Environment.OSVersion.Platform}");
        _prompt.AppendLine($"- Working Directory: {Environment.CurrentDirectory}");
        _prompt.AppendLine($"- Current Date: {DateTime.Now:yyyy-MM-dd}");
        _prompt.AppendLine($"- Host: {Environment.MachineName}");
        _prompt.AppendLine($"- User: {Environment.UserName}");
        _prompt.AppendLine();
    }

    private void AddAvailableTools(IEnumerable<ToolRegistration> tools)
    {
        _prompt.AppendLine("## Available Tools");
        _prompt.AppendLine("You have access to the following tools that you can invoke to complete tasks:");
        _prompt.AppendLine();
        
        // Group tools by category for better organization
        var groupedTools = tools.GroupBy(t => t.Metadata.Category)
                                .OrderBy(g => g.Key);
        
        foreach (var group in groupedTools)
        {
            _prompt.AppendLine($"### {group.Key} Tools");
            _prompt.AppendLine();
            
            foreach (var tool in group.OrderBy(t => t.Metadata.Name))
            {
                _prompt.AppendLine($"#### `{tool.Metadata.Id}`");
                _prompt.AppendLine($"**Name:** {tool.Metadata.Name}");
                _prompt.AppendLine($"**Description:** {tool.Metadata.Description}");
                
                if (tool.Metadata.Parameters.Any())
                {
                    _prompt.AppendLine("**Parameters:**");
                    foreach (var param in tool.Metadata.Parameters)
                    {
                        var required = param.Required ? " **[Required]**" : " [Optional]";
                        var typeInfo = FormatParameterType(param.Type);
                        
                        _prompt.AppendLine($"  - `{param.Name}` ({typeInfo}): {param.Description}{required}");
                        
                        // Add constraints if present
                        if (param.MinValue != null || param.MaxValue != null)
                        {
                            var constraints = new List<string>();
                            if (param.MinValue != null) constraints.Add($"min: {param.MinValue}");
                            if (param.MaxValue != null) constraints.Add($"max: {param.MaxValue}");
                            _prompt.AppendLine($"    Constraints: {string.Join(", ", constraints)}");
                        }
                        
                        if (param.AllowedValues?.Any() == true)
                        {
                            _prompt.AppendLine($"    Allowed values: {string.Join(", ", param.AllowedValues.Select(v => $"`{v}`"))}");
                        }
                    }
                }
                else
                {
                    _prompt.AppendLine("**Parameters:** None required");
                }
                
                // Add examples if available
                if (tool.Metadata.Examples?.Any() == true)
                {
                    _prompt.AppendLine("**Examples:**");
                    foreach (var example in tool.Metadata.Examples)
                    {
                        _prompt.AppendLine($"  - {example.Description}");
                        if (example.Parameters?.Any() == true)
                        {
                            var paramsJson = System.Text.Json.JsonSerializer.Serialize(example.Parameters);
                            _prompt.AppendLine($"    Parameters: `{paramsJson}`");
                        }
                    }
                }
                
                _prompt.AppendLine();
            }
        }
    }

    private void AddWorkflowGuidelines()
    {
        _prompt.AppendLine("## Workflow Guidelines");
        _prompt.AppendLine("- Break down complex tasks into smaller, manageable steps");
        _prompt.AppendLine("- Use tools in sequence when necessary to achieve the goal");
        _prompt.AppendLine("- Provide clear explanations of what you're doing and why");
        _prompt.AppendLine("- Handle errors gracefully and suggest alternatives when tools fail");
        _prompt.AppendLine("- Verify results before claiming success");
        _prompt.AppendLine();
    }

    private void AddToolUsageInstructions()
    {
        _prompt.AppendLine("## Tool Usage Instructions");
        _prompt.AppendLine("When you need to use a tool to complete a task, call it directly using the following JSON format:");
        _prompt.AppendLine("```json");
        _prompt.AppendLine("{");
        _prompt.AppendLine("  \"tool\": \"tool_id\",");
        _prompt.AppendLine("  \"parameters\": {");
        _prompt.AppendLine("    \"param_name\": \"value\"");
        _prompt.AppendLine("  }");
        _prompt.AppendLine("}");
        _prompt.AppendLine("```");
        _prompt.AppendLine();
        _prompt.AppendLine("Use the appropriate tool based on the user's request. For example:");
        _prompt.AppendLine("- If asked to list files or directories, use `list_directory`");
        _prompt.AppendLine("- If asked to read a file, use `read_file`");
        _prompt.AppendLine("- If asked about system information, use `system_info` or `process_info`");
        _prompt.AppendLine("- If asked to SAVE or CREATE a file, use `write_file`");
        _prompt.AppendLine();
        _prompt.AppendLine("IMPORTANT: When users ask you to write, show, or display code/text WITHOUT specifically asking to save it to a file:");
        _prompt.AppendLine("- Display the code/text directly in your response");
        _prompt.AppendLine("- Do NOT use the write_file tool unless explicitly asked to save/create a file");
        _prompt.AppendLine("- Examples: 'write a fibonacci program', 'show me a python script', 'create a function' - display inline");
        _prompt.AppendLine("- Examples: 'save this to file.py', 'create a file named test.js' - use write_file tool");
        _prompt.AppendLine();
        _prompt.AppendLine("Always use tools when they can help fulfill the user's request rather than explaining what you would do.");
        _prompt.AppendLine();
    }

    private void AddCriticalInstructions()
    {
        _prompt.AppendLine("## CRITICAL INSTRUCTIONS");
        _prompt.AppendLine();
        _prompt.AppendLine("### 1. Tool Execution Rules");
        _prompt.AppendLine("- Return ONLY the JSON object when invoking a tool - no explanatory text before or after");
        _prompt.AppendLine("- NEVER claim success or describe results until you receive [Tool Results]");
        _prompt.AppendLine("- Base your response on ACTUAL tool execution results, not assumptions");
        _prompt.AppendLine("- If a tool fails, explain the error and suggest alternatives");
        _prompt.AppendLine("- You can chain multiple tool calls if needed to complete a task");
        _prompt.AppendLine();
        
        _prompt.AppendLine("### 2. File Operations");
        _prompt.AppendLine("- **List files:** Use `list_directory` with the path parameter");
        _prompt.AppendLine("- **Read files:** Use `read_file` with the file_path parameter");
        _prompt.AppendLine("- **Write files:** Use `write_file` with file_path and content parameters");
        _prompt.AppendLine("- **Copy files:** First check if destination directory exists, create if needed with `create_directory`, then use `copy_file`");
        _prompt.AppendLine("- **Move files:** Use `move_file` with source and destination parameters");
        _prompt.AppendLine("- **Delete files:** Use `delete_file` with the file_path parameter");
        _prompt.AppendLine("- Always verify paths and check for existence before operations");
        _prompt.AppendLine();
        
        _prompt.AppendLine("### 3. Response Formatting");
        _prompt.AppendLine("- Use markdown for formatting responses when appropriate");
        _prompt.AppendLine("- Include code blocks with proper language tags for code snippets");
        _prompt.AppendLine("- Be concise but thorough in explanations");
        _prompt.AppendLine("- Group related information logically");
        _prompt.AppendLine();
        
        _prompt.AppendLine("### 4. Error Handling");
        _prompt.AppendLine("- If a tool returns an error, acknowledge it and explain what went wrong");
        _prompt.AppendLine("- Suggest alternative approaches when a tool fails");
        _prompt.AppendLine("- Never pretend a tool succeeded when it didn't");
        _prompt.AppendLine("- Ask for clarification if parameters are missing or unclear");
        _prompt.AppendLine();
        
        _prompt.AppendLine("### 5. Security and Privacy");
        _prompt.AppendLine("- Never expose sensitive information like passwords, API keys, or tokens");
        _prompt.AppendLine("- Be cautious with file system operations");
        _prompt.AppendLine("- Respect user privacy and data");
        _prompt.AppendLine("- Warn users about potentially dangerous operations");
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