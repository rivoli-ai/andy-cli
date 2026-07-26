using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Commands.Custom;
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
        new InlineCommandHelp.CommandInfo { Name = "permissions", Description = "Review and manage tool permission rules", Aliases = new[] { "perms", "perm" } },
        new InlineCommandHelp.CommandInfo { Name = "skills", Description = "List, inspect, and enable/disable agent skills", Aliases = new[] { "skill" } },
        new InlineCommandHelp.CommandInfo { Name = "commands", Description = "List, inspect, and reload Markdown slash commands", Aliases = new[] { "cmds" } },
        new InlineCommandHelp.CommandInfo { Name = "theme", Description = "List, switch, or toggle transparency of the UI theme", Aliases = new[] { "themes" } },
        new InlineCommandHelp.CommandInfo { Name = "clear", Description = "Clear conversation history", Aliases = Array.Empty<string>() },
        new InlineCommandHelp.CommandInfo { Name = "restart", Description = "Restart the session with a fresh conversation context", Aliases = Array.Empty<string>() },
        new InlineCommandHelp.CommandInfo { Name = "sessions", Description = "List saved sessions that can be resumed", Aliases = Array.Empty<string>() },
        new InlineCommandHelp.CommandInfo { Name = "resume", Description = "Resume a saved session (most recent when no id is given)", Aliases = Array.Empty<string>() },
        new InlineCommandHelp.CommandInfo { Name = "help", Description = "Show help information", Aliases = new[] { "?" } },
        new InlineCommandHelp.CommandInfo { Name = "exit", Description = "Exit the application", Aliases = new[] { "quit", "bye" } }
    };

    /// <summary>
    /// Additional names the interactive dispatcher in Program.cs handles but that are not
    /// listed in the inline help (undocumented or experimental toggles). They still count as
    /// built-in for shadowing purposes.
    /// </summary>
    private static readonly string[] UnlistedBuiltIns = { "auto", "yolo", "?" };

    /// <summary>
    /// Every name and alias that belongs to a built-in command. A Markdown command file whose
    /// name collides with one of these is rejected at discovery time (issue #281), so a
    /// checked-in template can never repoint <c>/permissions</c> or <c>/exit</c>.
    /// </summary>
    public static IReadOnlyCollection<string> ReservedCommandNames { get; } =
        CreateInlineHelpCommands()
            .SelectMany(c => new[] { c.Name }.Concat(c.Aliases))
            .Concat(UnlistedBuiltIns)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The built-in inline-help entries plus one entry per discovered Markdown command, so the
    /// autocomplete list under the prompt shows custom commands with their source. Built-ins
    /// stay first; custom commands follow in the catalog's stable name order.
    /// </summary>
    public static InlineCommandHelp.CommandInfo[] CreateInlineHelpCommands(
        IEnumerable<CustomCommandDefinition>? customCommands)
    {
        var builtIns = CreateInlineHelpCommands();
        if (customCommands is null)
            return builtIns;

        var custom = customCommands
            .Where(c => !ReservedCommandNames.Contains(c.Name))
            .Select(c => new InlineCommandHelp.CommandInfo
            {
                Name = c.Name,
                Description = $"[{c.SourceLabel}] {c.Description}",
                Aliases = c.Name.Contains(':') ? new[] { c.SlashPathForm } : Array.Empty<string>(),
            })
            .ToArray();

        return custom.Length == 0 ? builtIns : builtIns.Concat(custom).ToArray();
    }
}
