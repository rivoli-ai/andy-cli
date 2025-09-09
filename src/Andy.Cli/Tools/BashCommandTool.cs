using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Tools;

/// <summary>
/// Tool for handling bash/shell commands in the conversation.
/// This tool does NOT execute commands - it just captures them for display.
/// </summary>
public class BashCommandTool : ITool
{
    private readonly ILogger<BashCommandTool>? _logger;

    public ToolMetadata Metadata { get; } = new()
    {
        Id = "bash",
        Name = "Bash Command",
        Description = "Capture bash/shell commands for display (does not execute)",
        Version = "1.0.0",
        Category = ToolCategory.System,
        RequiredPermissions = ToolPermissionFlags.None,
        Parameters = new List<ToolParameter>
        {
            new()
            {
                Name = "command",
                Description = "The bash command to capture",
                Type = "string",
                Required = true
            },
            new()
            {
                Name = "description",
                Description = "Optional description of what the command does",
                Type = "string",
                Required = false
            }
        },
        Examples = new List<ToolExample>
        {
            new()
            {
                Name = "Compile C# program",
                Description = "Show how to compile a C# program",
                Parameters = new Dictionary<string, object?>
                {
                    ["command"] = "csc Program.cs -out:HelloWorld.exe",
                    ["description"] = "Compile C# source file to executable"
                }
            },
            new()
            {
                Name = "Run program",
                Description = "Show how to run a program",
                Parameters = new Dictionary<string, object?>
                {
                    ["command"] = "./HelloWorld.exe"
                }
            }
        }
    };

    public bool IsEnabled { get; set; } = true;

    // Parameterless constructor required for metadata discovery via Activator.CreateInstance
    public BashCommandTool()
    {
        _logger = null;
    }

    public BashCommandTool(ILogger<BashCommandTool>? logger = null)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(
        Dictionary<string, object?> parameters,
        ToolExecutionContext context)
    {
        try
        {
            // Extract parameters
            var command = parameters.GetValueOrDefault("command")?.ToString();
            var description = parameters.GetValueOrDefault("description")?.ToString();

            if (string.IsNullOrWhiteSpace(command))
            {
                return new ToolResult
                {
                    IsSuccessful = false,
                    ErrorMessage = "Command parameter is required"
                };
            }

            _logger?.LogDebug("Captured bash command: {Command}", command);

            // Create a formatted response that shows the command
            var response = new
            {
                command = command,
                description = description ?? "Shell command",
                note = "This command was captured for display. To execute commands, use the system shell.",
                syntax = DetectCommandType(command)
            };

            // Return success with the command info
            var jsonOutput = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
            return new ToolResult
            {
                IsSuccessful = true,
                Data = response,
                Message = "Command captured successfully"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error capturing bash command");
            return new ToolResult
            {
                IsSuccessful = false,
                ErrorMessage = $"Failed to capture command: {ex.Message}"
            };
        }
    }

    private string DetectCommandType(string command)
    {
        // Detect the type of command for proper syntax highlighting
        if (command.StartsWith("dotnet "))
            return "dotnet-cli";
        if (command.StartsWith("csc ") || command.EndsWith(".exe"))
            return "csharp-build";
        if (command.StartsWith("npm ") || command.StartsWith("yarn ") || command.StartsWith("pnpm "))
            return "nodejs";
        if (command.StartsWith("git "))
            return "git";
        if (command.StartsWith("cd ") || command.StartsWith("ls ") || command.StartsWith("pwd") ||
            command.StartsWith("mkdir ") || command.StartsWith("rm ") || command.StartsWith("cp ") ||
            command.StartsWith("mv "))
            return "shell-builtin";

        return "bash";
    }

    public void Validate(Dictionary<string, object?> parameters)
    {
        if (!parameters.ContainsKey("command") ||
            string.IsNullOrWhiteSpace(parameters["command"]?.ToString()))
        {
            throw new ArgumentException("Command parameter is required and cannot be empty");
        }
    }

    public Task InitializeAsync(Dictionary<string, object?>? configuration = null, CancellationToken cancellationToken = default)
    {
        // No initialization needed
        return Task.CompletedTask;
    }

    public IList<string> ValidateParameters(Dictionary<string, object?> parameters)
    {
        var errors = new List<string>();

        if (!parameters.ContainsKey("command") ||
            string.IsNullOrWhiteSpace(parameters["command"]?.ToString()))
        {
            errors.Add("Command parameter is required and cannot be empty");
        }

        return errors;
    }

    public bool CanExecuteWithPermissions(ToolPermissions permissions)
    {
        // This tool doesn't execute anything, just captures for display
        return true;
    }

    public Task DisposeAsync(CancellationToken cancellationToken = default)
    {
        // No resources to dispose
        return Task.CompletedTask;
    }
}