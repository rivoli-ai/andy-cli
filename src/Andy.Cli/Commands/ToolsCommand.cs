using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Andy.Tools.Core;
using Microsoft.Extensions.DependencyInjection;
using Andy.Cli.Widgets;

namespace Andy.Cli.Commands;

/// <summary>
/// Command for managing and listing available tools
/// </summary>
public class ToolsCommand : ICommand
{
    private readonly IServiceProvider _serviceProvider;
    private IToolRegistry? _toolRegistry;

    public string Name => "tools";
    public string Description => "Manage and list available AI tools";
    public string[] Aliases => new[] { "tool", "t" };

    public ToolsCommand(IServiceProvider? serviceProvider = null)
    {
        _serviceProvider = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
    }

    public async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
        {
            args = new[] { "list" };
        }

        var subcommand = args[0].ToLowerInvariant();
        
        return subcommand switch
        {
            "list" or "ls" => await ListToolsAsync(args.Skip(1).ToArray(), cancellationToken),
            "info" => await ShowToolInfoAsync(args.Skip(1).ToArray(), cancellationToken),
            "execute" or "exec" or "run" => await ExecuteToolAsync(args.Skip(1).ToArray(), cancellationToken),
            "help" or "?" => ShowHelp(),
            _ => CommandResult.Failure($"Unknown subcommand: {subcommand}. Use 'tools help' for usage information.")
        };
    }

    private async Task<CommandResult> ListToolsAsync(string[] args, CancellationToken cancellationToken)
    {
        var registry = GetToolRegistry();
        if (registry == null)
        {
            return CommandResult.Failure("Tool registry is not available. Tools may not be properly registered.");
        }

        var tools = registry.Tools;
        if (!tools.Any())
        {
            return CommandResult.CreateSuccess("No tools are currently registered.");
        }

        var categoryFilter = args.Length > 0 ? args[0] : null;

        // Group by category
        var groupedTools = tools.GroupBy(t => t.Metadata.Category).OrderBy(g => g.Key).ToList();

        // Filter by category if specified
        if (!string.IsNullOrEmpty(categoryFilter))
        {
            groupedTools = groupedTools.Where(g => 
                g.Key.ToString().Contains(categoryFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!groupedTools.Any())
            {
                return CommandResult.CreateSuccess($"No tools found in category matching '{categoryFilter}'.");
            }
        }

        var result = new StringBuilder();
        result.AppendLine($"Available Tools ({tools.Count} total):");
        result.AppendLine();

        foreach (var group in groupedTools)
        {
            result.AppendLine($"{group.Key} Tools ({group.Count()}):");
            
            foreach (var tool in group.OrderBy(t => t.Metadata.Name))
            {
                var status = tool.IsEnabled ? "[OK]" : "[X]";
                result.AppendLine($"  {status} {tool.Metadata.Name} (ID: {tool.Metadata.Id})");
                result.AppendLine($"      {tool.Metadata.Description}");
                
                if (tool.Metadata.Parameters.Any())
                {
                    var requiredParams = tool.Metadata.Parameters.Where(p => p.Required).Select(p => p.Name);
                    var optionalParams = tool.Metadata.Parameters.Where(p => !p.Required).Select(p => p.Name);
                    
                    if (requiredParams.Any())
                    {
                        result.AppendLine($"      Required: {string.Join(", ", requiredParams)}");
                    }
                    
                    if (optionalParams.Any())
                    {
                        result.AppendLine($"      Optional: {string.Join(", ", optionalParams)}");
                    }
                }
            }
            
            result.AppendLine();
        }

        result.AppendLine("Tools are automatically available to the AI and will be called when needed.");
        result.AppendLine("Use 'tools info <tool_id>' for detailed information about a specific tool.");
        result.AppendLine("Example: tools info read_file");

        return CommandResult.CreateSuccess(result.ToString());
    }

    private async Task<CommandResult> ShowToolInfoAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            return CommandResult.Failure("Usage: tools info <tool_id>\nExample: tools info read_file\n\nUse 'tools list' to see all available tool IDs.");
        }

        // Join all args in case the tool name has spaces (e.g., "Copy File")
        var toolName = string.Join(" ", args).Trim('"', ' ');
        var registry = GetToolRegistry();
        
        if (registry == null)
        {
            return CommandResult.Failure("Tool registry is not available.");
        }

        var tool = registry.GetTool(toolName);
        if (tool == null)
        {
            // Try to find by name instead of ID (more flexible matching)
            tool = registry.Tools.FirstOrDefault(t => 
                t.Metadata.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase) ||
                t.Metadata.Name.Replace(" ", "").Equals(toolName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) ||
                t.Metadata.Id.Replace("_", "").Equals(toolName.Replace(" ", "").Replace("_", ""), StringComparison.OrdinalIgnoreCase));
            
            if (tool == null)
            {
                // Suggest similar tools
                var similarTools = registry.Tools
                    .Where(t => t.Metadata.Name.Contains(toolName, StringComparison.OrdinalIgnoreCase) ||
                               t.Metadata.Id.Contains(toolName, StringComparison.OrdinalIgnoreCase))
                    .Take(3)
                    .ToList();
                
                var message = $"Tool '{toolName}' not found.";
                if (similarTools.Any())
                {
                    message += "\n\nDid you mean one of these?";
                    foreach (var similar in similarTools)
                    {
                        message += $"\n  - {similar.Metadata.Name} (ID: {similar.Metadata.Id})";
                    }
                }
                message += "\n\nUse 'tools list' to see all available tools with their IDs.";
                return CommandResult.Failure(message);
            }
        }

        var result = new StringBuilder();
        var metadata = tool.Metadata;
        
        result.AppendLine($"Tool Information: {metadata.Name}");
        result.AppendLine(new string('─', 40));
        result.AppendLine($"ID: {metadata.Id}");
        result.AppendLine($"Name: {metadata.Name}");
        result.AppendLine($"Version: {metadata.Version}");
        result.AppendLine($"Category: {metadata.Category}");
        result.AppendLine($"Status: {(tool.IsEnabled ? "Enabled" : "Disabled")}");
        result.AppendLine();
        
        result.AppendLine($"Description:");
        result.AppendLine($"  {metadata.Description}");
        result.AppendLine();
        
        if (metadata.RequiredPermissions != ToolPermissionFlags.None)
        {
            result.AppendLine($"Required Permissions: {metadata.RequiredPermissions}");
            result.AppendLine();
        }
        
        if (metadata.Parameters.Any())
        {
            result.AppendLine("Parameters:");
            foreach (var param in metadata.Parameters)
            {
                var required = param.Required ? "[Required]" : "[Optional]";
                result.AppendLine($"  {param.Name} {required}");
                result.AppendLine($"    Type: {param.Type}");
                result.AppendLine($"    Description: {param.Description}");
                
                if (param.DefaultValue != null)
                {
                    result.AppendLine($"    Default: {param.DefaultValue}");
                }
                
                if (param.AllowedValues != null && param.AllowedValues.Any())
                {
                    result.AppendLine($"    Allowed values: {string.Join(", ", param.AllowedValues)}");
                }
                
                if (!string.IsNullOrEmpty(param.Pattern))
                {
                    result.AppendLine($"    Validation: {param.Pattern}");
                }
            }
        }
        
        if (metadata.Examples.Any())
        {
            result.AppendLine();
            result.AppendLine("Examples:");
            foreach (var example in metadata.Examples)
            {
                result.AppendLine($"  {example.Name}:");
                result.AppendLine($"    {example.Description}");
                
                if (example.Parameters.Any())
                {
                    result.AppendLine($"    Parameters: {string.Join(", ", example.Parameters.Select(p => $"{p.Key}={p.Value?.ToString() ?? "null"}"))}");
                }
                
                if (example.ExpectedOutput != null)
                {
                    result.AppendLine($"    Expected output: {example.ExpectedOutput}");
                }
            }
        }
        
        if (metadata.Tags.Any())
        {
            result.AppendLine();
            result.AppendLine($"Tags: {string.Join(", ", metadata.Tags)}");
        }

        return CommandResult.CreateSuccess(result.ToString());
    }

    private async Task<CommandResult> ExecuteToolAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            return CommandResult.Failure("Usage: tools execute <tool_name> [parameters...]");
        }

        var toolName = args[0];
        var registry = GetToolRegistry();
        
        if (registry == null)
        {
            return CommandResult.Failure("Tool registry is not available.");
        }

        var toolReg = registry.GetTool(toolName);
        if (toolReg == null)
        {
            // Try to find by name instead of ID
            toolReg = registry.Tools.FirstOrDefault(t => 
                t.Metadata.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
            
            if (toolReg == null)
            {
                return CommandResult.Failure($"Tool '{toolName}' not found.");
            }
        }

        if (!toolReg.IsEnabled)
        {
            return CommandResult.Failure($"Tool '{toolName}' is disabled.");
        }

        // Parse parameters from remaining args
        var parameters = new Dictionary<string, object?>();
        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            var parts = arg.Split('=', 2);
            
            if (parts.Length == 2)
            {
                parameters[parts[0]] = parts[1];
            }
            else
            {
                // Treat as positional parameter
                parameters[$"arg{i}"] = arg;
            }
        }

        try
        {
            // Create tool instance
            var tool = registry.CreateTool(toolReg.Metadata.Id, _serviceProvider);
            if (tool == null)
            {
                return CommandResult.Failure($"Failed to create tool instance for '{toolName}'.");
            }

            // Execute the tool
            var executor = _serviceProvider.GetService<IToolExecutor>();
            if (executor == null)
            {
                // Fallback to direct execution
                var context = new ToolExecutionContext
                {
                    CancellationToken = cancellationToken
                };
                var result = await tool.ExecuteAsync(parameters, context);
                
                if (result.IsSuccessful)
                {
                    return CommandResult.CreateSuccess($"Tool executed successfully:\n{result.Output}");
                }
                else
                {
                    return CommandResult.Failure($"Tool execution failed: {result.Error}");
                }
            }
            else
            {
                var context = new ToolExecutionContext
                {
                    CancellationToken = cancellationToken
                };
                
                var result = await executor.ExecuteAsync(toolReg.Metadata.Id, parameters, context);
                
                if (result.IsSuccessful)
                {
                    return CommandResult.CreateSuccess($"Tool executed successfully:\n{result.Output}");
                }
                else
                {
                    return CommandResult.Failure($"Tool execution failed: {result.Error}");
                }
            }
        }
        catch (Exception ex)
        {
            return CommandResult.Failure($"Error executing tool: {ex.Message}");
        }
    }

    private CommandResult ShowHelp()
    {
        var help = new StringBuilder();
        help.AppendLine("Tools Command Usage:");
        help.AppendLine();
        help.AppendLine("tools list [category]     - List all available tools, optionally filtered by category");
        help.AppendLine("tools info <tool_name>    - Show detailed information about a specific tool");
        help.AppendLine("tools execute <tool_name> [params...] - Execute a tool with parameters");
        help.AppendLine("tools help                - Show this help message");
        help.AppendLine();
        help.AppendLine("Categories: FileSystem, TextProcessing, System, Web, Utility, Productivity, Git, Development");
        help.AppendLine();
        help.AppendLine("Examples:");
        help.AppendLine("  tools list                    - List all tools");
        help.AppendLine("  tools list FileSystem         - List only FileSystem tools");
        help.AppendLine("  tools info read_file          - Show info about the read_file tool");
        help.AppendLine("  tools execute read_file path=/etc/hosts - Execute read_file tool");
        
        return CommandResult.CreateSuccess(help.ToString());
    }

    private IToolRegistry? GetToolRegistry()
    {
        if (_toolRegistry == null)
        {
            _toolRegistry = _serviceProvider.GetService<IToolRegistry>();
        }
        return _toolRegistry;
    }

    public ToolListItem CreateToolListItem()
    {
        var item = new ToolListItem("Available Tools");
        var registry = GetToolRegistry();
        
        if (registry != null)
        {
            var groupedTools = registry.Tools
                .GroupBy(t => t.Metadata.Category)
                .OrderBy(g => g.Key);
            
            foreach (var group in groupedTools)
            {
                item.AddCategory(group.Key.ToString());
                
                foreach (var tool in group.OrderBy(t => t.Metadata.Name))
                {
                    item.AddTool(
                        tool.Metadata.Name,
                        tool.Metadata.Description,
                        tool.IsEnabled,
                        tool.Metadata.RequiredPermissions,
                        tool.Metadata.Id
                    );
                }
            }
        }
        
        return item;
    }
}