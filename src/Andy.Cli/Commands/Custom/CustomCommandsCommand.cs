using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Commands.Custom;

/// <summary>
/// <c>/commands</c> - list, inspect, and reload the Markdown-defined slash commands
/// discovered from <c>~/.andy/commands</c> and <c>&lt;workspace&gt;/.andy/commands</c>
/// (issue #281).
/// </summary>
public sealed class CustomCommandsCommand : ICommand
{
    private readonly CustomCommandCatalog _catalog;

    public CustomCommandsCommand(CustomCommandCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string Name => "commands";
    public string Description => "List, inspect, and reload Markdown slash commands";
    public string[] Aliases => new[] { "cmds" };

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var sub = (args.Length > 0 ? args[0] : "list").ToLowerInvariant();
        return Task.FromResult(sub switch
        {
            "list" or "ls" => List(),
            "info" or "show" => Info(args),
            "reload" or "refresh" => Reload(),
            "diagnostics" or "diag" => Diagnostics(),
            "help" or "?" or "-h" or "--help" => Help(),
            _ => CommandResult.Failure($"Unknown subcommand: {sub}. Use 'commands help' for usage."),
        });
    }

    private CommandResult List()
    {
        var commands = _catalog.Commands;
        var sb = new StringBuilder();

        if (commands.Count == 0)
        {
            sb.AppendLine("No Markdown commands found.");
            sb.AppendLine();
            sb.AppendLine("Create a .md file in one of these directories to define one:");
            foreach (var root in _catalog.Roots)
                sb.AppendLine($"  {root}");
            sb.AppendLine();
            sb.AppendLine("The file name becomes the command name; nested directories become");
            sb.AppendLine("colon-separated segments (git/commit.md -> /git:commit).");
        }
        else
        {
            sb.AppendLine($"Markdown commands ({commands.Count}):");
            sb.AppendLine();
            var nameWidth = Math.Min(28, commands.Max(c => c.Name.Length + 1));
            foreach (var command in commands)
            {
                sb.AppendLine($"  {("/" + command.Name).PadRight(nameWidth)}  [{command.SourceLabel}]  {command.Description}");
            }
            sb.AppendLine();
            sb.AppendLine("Use 'commands info <name>' for the template and file path.");
        }

        var diagnostics = _catalog.Diagnostics;
        if (diagnostics.Count > 0)
            sb.AppendLine($"{diagnostics.Count} discovery diagnostic(s); run 'commands diagnostics' to view.");

        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private CommandResult Info(string[] args)
    {
        if (args.Length < 2)
            return CommandResult.Failure("Usage: commands info <name>");

        var command = _catalog.Find(args[1]);
        if (command is null)
            return CommandResult.Failure($"No Markdown command named '{args[1]}'. Run 'commands list' to see what is available.");

        var sb = new StringBuilder();
        sb.AppendLine($"Command: /{command.Name}");
        sb.AppendLine($"  Description : {command.Description}");
        sb.AppendLine($"  Source      : {command.SourceLabel}");
        sb.AppendLine($"  File        : {command.FilePath}");
        if (command.Provider != null)
            sb.AppendLine($"  Provider    : {command.Provider} (advisory metadata)");
        if (command.Model != null)
            sb.AppendLine($"  Model       : {command.Model} (advisory metadata)");
        if (command.Mode != null)
            sb.AppendLine($"  Mode        : {command.Mode} (advisory metadata)");
        sb.AppendLine($"  Arguments   : {DescribeArguments(command)}");
        if (command.ShadowedFilePaths.Count > 0)
        {
            sb.AppendLine("  Shadows     :");
            foreach (var path in command.ShadowedFilePaths)
                sb.AppendLine($"    {path}");
        }
        sb.AppendLine();
        sb.AppendLine("Template:");
        foreach (var line in command.Template.Split('\n'))
            sb.AppendLine("  " + line);
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private static string DescribeArguments(CustomCommandDefinition command)
    {
        var parts = new List<string>();
        if (command.UsesArguments) parts.Add("$ARGUMENTS");
        if (command.MaxPositional > 0)
            parts.Add(command.MaxPositional == 1 ? "$1" : $"$1..${command.MaxPositional}");
        return parts.Count == 0 ? "none (arguments are ignored)" : string.Join(", ", parts);
    }

    private CommandResult Reload()
    {
        var commands = _catalog.Reload();
        var diagnostics = _catalog.Diagnostics;
        var suffix = diagnostics.Count > 0
            ? $" {diagnostics.Count} diagnostic(s); run 'commands diagnostics' to view."
            : "";
        return CommandResult.CreateSuccess($"Markdown commands reloaded: {commands.Count} command(s).{suffix}");
    }

    private CommandResult Diagnostics()
    {
        var diagnostics = _catalog.Diagnostics;
        if (diagnostics.Count == 0)
            return CommandResult.CreateSuccess("No Markdown command diagnostics.");

        var sb = new StringBuilder();
        sb.AppendLine($"Markdown command diagnostics ({diagnostics.Count}):");
        foreach (var d in diagnostics)
        {
            sb.AppendLine($"  [{d.Severity}] {d.Path}");
            sb.AppendLine($"    {d.Message}");
        }
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private CommandResult Help()
    {
        var sb = new StringBuilder();
        sb.AppendLine("commands - list, inspect, and reload Markdown slash commands");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine("  commands [list]         Show discovered Markdown commands and their source");
        sb.AppendLine("  commands info <name>    Show a command's file, metadata, and template");
        sb.AppendLine("  commands reload         Re-scan the command roots (no restart needed)");
        sb.AppendLine("  commands diagnostics    Show problems found during discovery");
        sb.AppendLine("  commands help           Show this help");
        sb.AppendLine();
        sb.AppendLine("Commands are Markdown files (optional YAML frontmatter with description/");
        sb.AppendLine("provider/model/mode, then the prompt template) discovered from:");
        foreach (var root in _catalog.Roots)
            sb.AppendLine($"  {root}");
        sb.AppendLine("Project commands win over user commands; built-in command names cannot be");
        sb.AppendLine("redefined. Templates expand $ARGUMENTS, $1..$9, and $$ (a literal dollar).");
        sb.AppendLine();
        sb.AppendLine("A Markdown command only produces a prompt. It cannot run a shell, grant a");
        sb.AppendLine("permission, enable a tool, or bypass plan mode; the expanded prompt goes");
        sb.AppendLine("through the same path as anything you type yourself.");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }
}
