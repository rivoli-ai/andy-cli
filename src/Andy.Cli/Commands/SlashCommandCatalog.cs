using System;
using Andy.Cli.Widgets;

namespace Andy.Cli.Commands;

/// <summary>
/// Catalog of the interactive-mode slash commands surfaced by the inline
/// command help (the list shown under the prompt while typing "/...").
/// Kept in one place so the help list, the dispatcher in Program.cs, and
/// the tests stay in sync.
/// </summary>
public static class SlashCommandCatalog
{
    public static InlineCommandHelp.CommandInfo[] CreateInlineHelpCommands() => new[]
    {
        new InlineCommandHelp.CommandInfo { Name = "model", Description = "Manage AI models (list, switch, info, test)", Aliases = new[] { "m" } },
        new InlineCommandHelp.CommandInfo { Name = "tools", Description = "Manage and list available tools", Aliases = new[] { "tool", "t" } },
        new InlineCommandHelp.CommandInfo { Name = "mcp", Description = "List MCP servers and connection status", Aliases = Array.Empty<string>() },
        new InlineCommandHelp.CommandInfo { Name = "theme", Description = "List, switch, or toggle transparency of the UI theme", Aliases = new[] { "themes" } },
        new InlineCommandHelp.CommandInfo { Name = "clear", Description = "Clear conversation history", Aliases = Array.Empty<string>() },
        new InlineCommandHelp.CommandInfo { Name = "restart", Description = "Restart the session with a fresh conversation context", Aliases = Array.Empty<string>() },
        new InlineCommandHelp.CommandInfo { Name = "sessions", Description = "List saved sessions that can be resumed", Aliases = Array.Empty<string>() },
        new InlineCommandHelp.CommandInfo { Name = "resume", Description = "Resume a saved session (most recent when no id is given)", Aliases = Array.Empty<string>() },
        new InlineCommandHelp.CommandInfo { Name = "help", Description = "Show help information", Aliases = new[] { "?" } },
        new InlineCommandHelp.CommandInfo { Name = "exit", Description = "Exit the application", Aliases = new[] { "quit", "bye" } }
    };
}
