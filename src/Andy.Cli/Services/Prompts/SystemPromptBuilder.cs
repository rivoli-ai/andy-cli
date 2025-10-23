using System.Text;

namespace Andy.Cli.Services.Prompts;

/// <summary>
/// Builds comprehensive system prompts with modular components.
/// </summary>
public class SystemPromptBuilder
{
    private readonly StringBuilder _prompt = new();
    private readonly List<ToolInfo> _tools = new();
    private string? _customInstructions;
    private string? _platform;
    private string? _workingDirectory;
    private DateTime? _currentDate;
    private TimeZoneInfo? _timeZone;

    public SystemPromptBuilder WithCoreMandates()
    {
        _prompt.AppendLine("You are an interactive CLI assistant specializing in software engineering tasks. Your primary goal is to help users efficiently and directly.");
        _prompt.AppendLine();
        _prompt.AppendLine("## Core Principles");
        _prompt.AppendLine();
        _prompt.AppendLine("- **Be Direct**: Answer questions directly. Don't ask for permission unless necessary.");
        _prompt.AppendLine("- **Be Smart About Tools**: Only use tools when you need to read files, write files, or execute commands. Don't use tools for things you can answer directly.");
        _prompt.AppendLine("- **Be Conversational When Appropriate**: For greetings, self-identification, or general questions, respond directly without calling any tools.");
        _prompt.AppendLine("- **Be an Agent**: Keep working until the user's request is fully resolved. Make reasonable assumptions and act decisively. Only ask for clarification when absolutely necessary for critical information.");
        _prompt.AppendLine();

        return this;
    }

    public SystemPromptBuilder WithWorkflowGuidelines()
    {
        _prompt.AppendLine("## Workflow Guidelines");
        _prompt.AppendLine();
        _prompt.AppendLine("- Break down complex tasks into smaller steps");
        _prompt.AppendLine("- Use tools in sequence when necessary to achieve the goal");
        _prompt.AppendLine("- Provide clear explanations of what you're doing and why");
        _prompt.AppendLine("- Handle errors gracefully and suggest alternatives when tools fail");
        _prompt.AppendLine();
        _prompt.AppendLine("## Tool Usage Instructions");
        _prompt.AppendLine();
        _prompt.AppendLine("When you need to use a tool to complete a task, call it directly using the function calling interface.");
        _prompt.AppendLine();
        _prompt.AppendLine("**DO use tools for:**");
        _prompt.AppendLine("- Reading or writing files");
        _prompt.AppendLine("- Executing shell commands");
        _prompt.AppendLine("- Searching codebases");
        _prompt.AppendLine("- System operations");
        _prompt.AppendLine();
        _prompt.AppendLine("**DO NOT use tools for:**");
        _prompt.AppendLine("- Simple greetings (\"hello\", \"hi\", \"hey\")");
        _prompt.AppendLine("- Questions about yourself (\"what model are you?\", \"who are you?\")");
        _prompt.AppendLine("- Math calculations (\"what is 2+2?\")");
        _prompt.AppendLine("- General knowledge questions");
        _prompt.AppendLine("- Explanations or code examples you can provide directly");
        _prompt.AppendLine();
        _prompt.AppendLine("**IMPORTANT**: When users ask you to write, show, or display code/text WITHOUT specifically asking to save it to a file:");
        _prompt.AppendLine("- Display the code/text directly in your response");
        _prompt.AppendLine("- Do NOT use the write_file tool unless explicitly asked to save/create a file");
        _prompt.AppendLine("- Examples: 'write a fibonacci program', 'show me a python script', 'create a function' - display inline");
        _prompt.AppendLine("- Examples: 'save this to file.py', 'create a file named test.js' - use write_file tool");
        _prompt.AppendLine();
        _prompt.AppendLine("**Best Practices:**");
        _prompt.AppendLine("- Read files before making changes to understand context");
        _prompt.AppendLine("- Explain destructive commands before running them");
        _prompt.AppendLine("- Use absolute paths with file operations");
        _prompt.AppendLine("- Run independent operations in parallel when possible");
        _prompt.AppendLine("- Always use tools when they can help fulfill the user's request rather than explaining what you would do");
        _prompt.AppendLine();

        return this;
    }

    /// <summary>
    /// Adds environment context including platform, working directory, and current date/time with timezone.
    /// </summary>
    public SystemPromptBuilder WithEnvironment(
        string platform,
        string workingDirectory,
        DateTime currentDate,
        TimeZoneInfo? timeZone = null)
    {
        _platform = platform;
        _workingDirectory = workingDirectory;
        _currentDate = currentDate;
        _timeZone = timeZone ?? TimeZoneInfo.Local;

        _prompt.AppendLine("## Environment Context");
        _prompt.AppendLine($"- Platform: {platform}");
        _prompt.AppendLine($"- Working Directory: {workingDirectory}");
        _prompt.AppendLine($"- Current Date/Time: {currentDate:yyyy-MM-dd HH:mm:ss}");
        _prompt.AppendLine($"- Timezone: {_timeZone.DisplayName} ({_timeZone.Id})");
        _prompt.AppendLine($"- UTC Offset: {_timeZone.GetUtcOffset(currentDate):hh\\:mm}");
        _prompt.AppendLine();

        return this;
    }

    /// <summary>
    /// Adds available tools to the prompt.
    /// </summary>
    public SystemPromptBuilder WithAvailableTools(IEnumerable<ToolInfo> tools)
    {
        var toolList = tools.ToList();
        _tools.AddRange(toolList);

        if (toolList.Any())
        {
            _prompt.AppendLine("## Available Tools");
            _prompt.AppendLine("You have access to the following tools:");
            _prompt.AppendLine();

            foreach (var tool in toolList)
            {
                _prompt.AppendLine($"### {tool.Name}");
                _prompt.AppendLine($"Description: {tool.Description}");
                if (tool.Parameters?.Any() == true)
                {
                    _prompt.AppendLine("Parameters:");
                    foreach (var param in tool.Parameters)
                    {
                        _prompt.Append($"  - {param.Name} ({param.Type}): {param.Description}");
                        if (param.IsRequired)
                        {
                            _prompt.Append(" [Required]");
                        }

                        _prompt.AppendLine();
                    }
                }

                _prompt.AppendLine();
            }
        }

        return this;
    }

    /// <summary>
    /// Adds custom instructions to the prompt.
    /// </summary>
    public SystemPromptBuilder WithCustomInstructions(string? instructions)
    {
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            _customInstructions = instructions;
            _prompt.AppendLine("## Custom Instructions");
            _prompt.AppendLine(instructions);
            _prompt.AppendLine();
        }

        return this;
    }

    /// <summary>
    /// Builds the final system prompt string.
    /// </summary>
    public string Build()
    {
        var result = new StringBuilder();

        // Add default sections if not already added
        if (!_prompt.ToString().Contains("Core Principles"))
        {
            WithCoreMandates();
        }

        if (!_prompt.ToString().Contains("Workflow Guidelines"))
        {
            WithWorkflowGuidelines();
        }

        result.Append(_prompt);

        return result.ToString().TrimEnd();
    }
}

public class ToolInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ToolParameterInfo> Parameters { get; set; } = new();
}

public class ToolParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
}
