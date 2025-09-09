using System;
using System.Linq;
using Andy.Cli.Commands;
using Andy.Cli.Widgets;
using Andy.Llm;

namespace Andy.Cli.Services;

/// <summary>
/// Sets up and configures the command palette with available commands
/// </summary>
public static class CommandPaletteSetup
{
    public static void ConfigureCommands(
        CommandPalette commandPalette,
        ModelCommand modelCommand,
        ToolsCommand toolsCommand,
        FeedView feed,
        Action<bool> setRunning,
        Func<LlmClient?> getCurrentClient,
        Action<LlmClient?> setCurrentClient,
        ConversationContext conversation)
    {
        commandPalette.SetCommands(new[]
        {
            new CommandPalette.CommandItem
            {
                Name = "Exit",
                Description = "Quit the application",
                Category = "General",
                Aliases = new[] { "quit", "exit", "bye", "q" },
                Action = args =>
                {
                    setRunning(false);
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "List Models",
                Description = "Show available AI models",
                Category = "Model",
                Aliases = new[] { "models", "list" },
                Action = async args =>
                {
                    var modelListItem = await modelCommand.CreateModelListItemAsync();
                    feed.AddItem(modelListItem);
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "Switch Model",
                Description = "Change AI provider/model",
                Category = "Model",
                Aliases = new[] { "switch", "change" },
                RequiredParams = new[] { "provider" },
                ParameterHint = "Example: cerebras or openai gpt-4o",
                Action = async args =>
                {
                    if (args.Length < 1)
                    {
                        feed.AddMarkdownRich("Usage: Switch Model <provider> [model]\nProviders: cerebras, openai, anthropic");
                    }
                    else
                    {
                        var result = await modelCommand.ExecuteAsync(new[] { "switch" }.Concat(args).ToArray());
                        feed.AddMarkdownRich(result.Message);
                        if (result.Success)
                        {
                            // Update the LLM client and reset conversation context
                            var newClient = modelCommand.GetCurrentClient();
                            setCurrentClient(newClient);
                            conversation.Clear();
                            conversation.SystemInstruction = "You are a helpful AI assistant. Keep your responses concise and helpful.";
                            feed.AddMarkdownRich($"*Note: Conversation context reset for {modelCommand.GetCurrentProvider()} model*");
                        }
                    }
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "Model Info",
                Description = "Show current model details",
                Category = "Model",
                Aliases = new[] { "info", "current" },
                Action = async args =>
                {
                    var result = await modelCommand.ExecuteAsync(new[] { "info" });
                    feed.AddMarkdownRich(result.Message);
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "Test Model",
                Description = "Test current model",
                Category = "Model",
                Aliases = new[] { "test" },
                Action = async args =>
                {
                    var result = await modelCommand.ExecuteAsync(new[] { "test" }.Concat(args).ToArray());
                    feed.AddMarkdownRich(result.Message);
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "List Tools",
                Description = "Show available AI tools",
                Category = "Tools",
                Aliases = new[] { "tools", "tool list" },
                Action = args =>
                {
                    var toolListItem = toolsCommand.CreateToolListItem();
                    feed.AddItem(toolListItem);
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "Tool Info",
                Description = "Show details about a specific tool",
                Category = "Tools",
                Aliases = new[] { "tool info", "tool details" },
                RequiredParams = new[] { "tool_id_or_name" },
                ParameterHint = "Enter tool ID (e.g., read_file, copy_file) or name (e.g., \"Copy File\")",
                GetAvailableOptions = () =>
                {
                    // Get all available tool IDs from the registry
                    var registry = toolsCommand.GetToolRegistry();
                    if (registry != null)
                    {
                        return registry.Tools
                            .OrderBy(t => t.Metadata.Category)
                            .ThenBy(t => t.Metadata.Name)
                            .Select(t => $"{t.Metadata.Id} - {t.Metadata.Name}")
                            .ToArray();
                    }
                    return Array.Empty<string>();
                },
                Action = async args =>
                {
                    if (args.Length < 1)
                    {
                        feed.AddMarkdownRich("Usage: Tool Info <tool_id_or_name>\n\nExamples:\n- copy_file\n- \"Copy File\"\n- json_processor\n\nUse /tools list to see all available tool IDs.");
                    }
                    else
                    {
                        var result = await toolsCommand.ExecuteAsync(new[] { "info" }.Concat(args).ToArray());
                        feed.AddMarkdownRich(result.Message);
                    }
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "Execute Tool",
                Description = "Run a tool with parameters",
                Category = "Tools",
                Aliases = new[] { "tool exec", "tool run" },
                RequiredParams = new[] { "tool_id", "params..." },
                ParameterHint = "Example: read_file file_path=/etc/hosts",
                GetAvailableOptions = () =>
                {
                    // Get all available tool IDs from the registry
                    var registry = toolsCommand.GetToolRegistry();
                    if (registry != null)
                    {
                        return registry.Tools
                            .OrderBy(t => t.Metadata.Category)
                            .ThenBy(t => t.Metadata.Name)
                            .Select(t => $"{t.Metadata.Id} - {t.Metadata.Name}")
                            .ToArray();
                    }
                    return Array.Empty<string>();
                },
                Action = async args =>
                {
                    if (args.Length < 1)
                    {
                        feed.AddMarkdownRich("Usage: Execute Tool <tool_name> [parameters...]");
                    }
                    else
                    {
                        var result = await toolsCommand.ExecuteAsync(new[] { "execute" }.Concat(args).ToArray());
                        feed.AddMarkdownRich(result.Message);
                    }
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "Clear",
                Description = "Clear the chat feed",
                Category = "General",
                Aliases = new[] { "cls", "clear" },
                Action = args =>
                {
                    feed.Clear();
                    feed.AddMarkdownRich("**Chat cleared.** Ready for a fresh start!");
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "Reset Context",
                Description = "Reset conversation context",
                Category = "Chat",
                Aliases = new[] { "reset", "new" },
                Action = args =>
                {
                    conversation.Clear();
                    conversation.SystemInstruction = "You are a helpful AI assistant. Keep your responses concise and helpful.";
                    feed.AddMarkdownRich("**Conversation context reset.** Starting fresh!");
                }
            },
            new CommandPalette.CommandItem
            {
                Name = "Toggle HUD",
                Description = "Show/hide performance HUD",
                Category = "General",
                Aliases = new[] { "hud", "debug" },
                Action = args =>
                {
                    // This will be handled in the main key handler
                    feed.AddMarkdownRich("Press **F2** to toggle the HUD");
                }
            }
        });
    }
}