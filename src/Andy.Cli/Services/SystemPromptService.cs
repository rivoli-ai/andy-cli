using System.Text;
using Andy.Tools.Core;
using System.IO;
using System.Linq;

namespace Andy.Cli.Services;

/// <summary>
/// Service for building comprehensive system prompts for AI conversation
/// </summary>
public class SystemPromptService
{
    private readonly StringBuilder _prompt = new();

    public string BuildSystemPrompt(IEnumerable<ToolRegistration> availableTools, string? modelName = null, string? providerName = null)
    {
        _prompt.Clear();
        
        AddCoreMandates();
        AddEnvironmentContext();
        AddAvailableTools(availableTools);
        AddWorkflowGuidelines();
        AddToolUsageInstructions(modelName, providerName);
        AddCriticalInstructions(modelName, providerName);
        
        return _prompt.ToString();
    }

    private void AddCoreMandates()
    {
        _prompt.AppendLine("You are an AI assistant with tool access. Be helpful, accurate, and concise.");
        _prompt.AppendLine($"Environment: {Environment.OSVersion.Platform}, {Environment.CurrentDirectory}, {DateTime.Now:yyyy-MM-dd}");
        _prompt.AppendLine();
        
        // Add project structure
        var projectTree = GetDirectoryTree(Environment.CurrentDirectory, maxDepth: 3);
        if (!string.IsNullOrEmpty(projectTree))
        {
            _prompt.AppendLine("## Current Project Structure");
            _prompt.AppendLine("```");
            _prompt.AppendLine(projectTree);
            _prompt.AppendLine("```");
            _prompt.AppendLine();
        }
        
        _prompt.AppendLine("## Best Practices");
        _prompt.AppendLine("- The project structure above shows available files and directories");
        _prompt.AppendLine("- Use this information to read actual files that exist");
        _prompt.AppendLine("- For deeper exploration beyond shown depth, use list_directory");
        _prompt.AppendLine("- Don't assume file paths - work with what's actually present");
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
        
        // Group tools by category for better organization
        var toolsByCategory = tools.GroupBy(t => t.Metadata.Category).OrderBy(g => g.Key);
        
        foreach (var category in toolsByCategory)
        {
            _prompt.AppendLine($"### {category.Key}");
            
            foreach (var tool in category.OrderBy(t => t.Metadata.Name))
            {
                _prompt.AppendLine($"- **{tool.Metadata.Id}**: {tool.Metadata.Description}");
                
                if (tool.Metadata.Parameters.Any())
                {
                    // Show exact parameter names and types
                    var paramDetails = new List<string>();
                    foreach (var param in tool.Metadata.Parameters)
                    {
                        var paramStr = param.Required ? $"`{param.Name}`*" : $"`{param.Name}`";
                        paramDetails.Add(paramStr);
                    }
                    _prompt.AppendLine($"  Parameters: {string.Join(", ", paramDetails)} (* = required)");
                    
                    // Add example for commonly used tools
                    if (tool.Metadata.Id == "read_file")
                    {
                        _prompt.AppendLine($"  Example: {{\"tool\":\"read_file\",\"parameters\":{{\"file_path\":\"/path/to/file.txt\"}}}}");
                    }
                    else if (tool.Metadata.Id == "list_directory")
                    {
                        _prompt.AppendLine($"  Example: {{\"tool\":\"list_directory\",\"parameters\":{{\"path\":\".\"}}}}");
                    }
                    else if (tool.Metadata.Id == "write_file")
                    {
                        _prompt.AppendLine($"  Example: {{\"tool\":\"write_file\",\"parameters\":{{\"file_path\":\"test.txt\",\"content\":\"Hello\"}}}}");
                    }
                }
                else
                {
                    _prompt.AppendLine($"  Parameters: none");
                }
            }
            _prompt.AppendLine();
        }
    }

    private void AddWorkflowGuidelines()
    {
        _prompt.AppendLine("## How Tools Work");
        _prompt.AppendLine("When you need to use a tool:");
        _prompt.AppendLine("1. Output ONLY the JSON: {\"tool\":\"tool_id\",\"parameters\":{}}");
        _prompt.AppendLine("2. I will execute the tool and respond with [Tool Results]");
        _prompt.AppendLine("3. Then continue with your response based on the actual results");
        _prompt.AppendLine();
        _prompt.AppendLine("## Example Workflow for Project Exploration");
        _prompt.AppendLine("When asked about a project or codebase:");
        _prompt.AppendLine("1. FIRST: {\"tool\":\"list_directory\",\"parameters\":{\"path\":\".\"}} to see what's available");
        _prompt.AppendLine("2. THEN: Based on actual files found, read specific files like README.md or .csproj files");
        _prompt.AppendLine("3. NEVER: Try to read files you haven't verified exist with list_directory");
        _prompt.AppendLine();
    }

    private void AddToolUsageInstructions(string? modelName, string? providerName)
    {
        // This section is now merged into workflow guidelines for brevity
    }

    private void AddCriticalInstructions(string? modelName, string? providerName)
    {
        _prompt.AppendLine("## CRITICAL: How to Use Tools");
        
        // Check if this is a Qwen model
        bool isQwenModel = modelName?.ToLowerInvariant().Contains("qwen") ?? false;
        
        if (isQwenModel)
        {
            _prompt.AppendLine("CRITICAL INSTRUCTIONS FOR QWEN MODEL:");
            _prompt.AppendLine();
            _prompt.AppendLine("1. ONLY use tools when explicitly needed to answer the user's question");
            _prompt.AppendLine("2. For greetings like 'hello' or 'hi', just respond with a greeting - NO TOOLS");
            _prompt.AppendLine("3. When you need to use a tool, you MUST output the tool call JSON:");
            _prompt.AppendLine();
            _prompt.AppendLine("   Format A (Preferred):");
            _prompt.AppendLine("   <tool_call>");
            _prompt.AppendLine("   {\"name\":\"tool_id\",\"arguments\":{\"param\":\"value\"}}");
            _prompt.AppendLine("   </tool_call>");
            _prompt.AppendLine();
            _prompt.AppendLine("   Format B (Alternative):");
            _prompt.AppendLine("   {\"tool\":\"tool_id\",\"parameters\":{\"param\":\"value\"}}");
            _prompt.AppendLine();
            _prompt.AppendLine("4. IMPORTANT: If you say you're going to do something (e.g., 'Let me list...'), ");
            _prompt.AppendLine("   you MUST output the corresponding tool call JSON immediately after");
            _prompt.AppendLine("5. Use ONE tool at a time - wait for results before using another");
            _prompt.AppendLine("6. NEVER output fake '[Tool Results]' or simulate tool execution");
            _prompt.AppendLine("7. NEVER just say what you're going to do without actually doing it");
            _prompt.AppendLine("8. Be concise - avoid repeating yourself or outputting corrupted text");
        }
        else
        {
            _prompt.AppendLine("To execute a tool, you MUST respond with ONLY this JSON format:");
            _prompt.AppendLine("```json");
            _prompt.AppendLine("{\"tool\":\"tool_id\",\"parameters\":{\"param_name\":\"value\"}}");
            _prompt.AppendLine("```");
        }
        _prompt.AppendLine();
        _prompt.AppendLine("## ABSOLUTELY FORBIDDEN:");
        _prompt.AppendLine("❌ NEVER output \"[Tool Results]\" - this is ONLY for system use");
        _prompt.AppendLine("❌ NEVER simulate or fake tool execution results");
        _prompt.AppendLine("❌ NEVER pretend a tool was called without actually calling it");
        _prompt.AppendLine("❌ NEVER make up file contents or directory listings");
        _prompt.AppendLine("❌ NEVER output fake JSON results wrapped in [Tool Results]");
        _prompt.AppendLine();
        _prompt.AppendLine("## REQUIRED:");
        _prompt.AppendLine("✓ ALWAYS make real tool calls using the exact JSON format above");
        _prompt.AppendLine("✓ ALWAYS wait for actual [Tool Results] from the system");
        _prompt.AppendLine("✓ ALWAYS use EXACT parameter names (e.g., `file_path` not `path` for read_file)");
        _prompt.AppendLine("✓ ALWAYS list_directory FIRST before trying to read files");
        _prompt.AppendLine();
        _prompt.AppendLine("## Common Parameter Names:");
        _prompt.AppendLine("- read_file: uses `file_path` (NOT `path`)");
        _prompt.AppendLine("- write_file: uses `file_path` and `content` (NOT `path` and `data`)");
        _prompt.AppendLine("- list_directory: uses `path` (NOT `directory` or `dir`)");
        _prompt.AppendLine("- copy_file: uses `source_path` and `destination_path`");
        _prompt.AppendLine("- move_file: uses `source_path` and `destination_path`");
        _prompt.AppendLine();
        _prompt.AppendLine("Remember: You cannot access the file system without tools. Never pretend you can.");
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
    
    private string GetDirectoryTree(string path, int maxDepth = 3, int currentDepth = 0, string prefix = "")
    {
        if (currentDepth >= maxDepth)
            return "";
            
        var result = new StringBuilder();
        var dirInfo = new DirectoryInfo(path);
        
        if (currentDepth == 0)
        {
            result.AppendLine(dirInfo.Name + "/");
        }
        
        try
        {
            // Get directories and files, filter out common ignored items
            var entries = dirInfo.GetFileSystemInfos()
                .Where(e => !ShouldIgnoreEntry(e.Name))
                .OrderBy(e => e is DirectoryInfo ? 0 : 1) // Directories first
                .ThenBy(e => e.Name)
                .Take(50) // Limit entries per directory to avoid huge trees
                .ToList();
            
            for (int i = 0; i < entries.Count; i++)
            {
                var isLast = i == entries.Count - 1;
                var entry = entries[i];
                var connector = isLast ? "└── " : "├── ";
                var extension = isLast ? "    " : "│   ";
                
                if (entry is DirectoryInfo dir)
                {
                    result.AppendLine($"{prefix}{connector}{dir.Name}/");
                    if (currentDepth < maxDepth - 1)
                    {
                        var subTree = GetDirectoryTree(dir.FullName, maxDepth, currentDepth + 1, prefix + extension);
                        result.Append(subTree);
                    }
                }
                else
                {
                    result.AppendLine($"{prefix}{connector}{entry.Name}");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        
        return result.ToString();
    }
    
    private bool ShouldIgnoreEntry(string name)
    {
        // Common directories and files to ignore
        var ignoredPatterns = new[] 
        { 
            ".git", ".vs", ".vscode", ".idea", 
            "bin", "obj", "node_modules", 
            ".DS_Store", "Thumbs.db",
            "TestResults", "packages", ".nuget",
            "__pycache__", ".pytest_cache", ".mypy_cache"
        };
        
        return ignoredPatterns.Any(pattern => 
            name.Equals(pattern, StringComparison.OrdinalIgnoreCase));
    }
}