using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Themes;

namespace Andy.Cli.Commands;

/// <summary>
/// Lists and switches the active UI theme, persisting the choice across sessions.
/// </summary>
public class ThemeCommand : ICommand
{
    private readonly ThemeMemoryService _themeMemory;

    public string Name => "theme";
    public string Description => "List and switch the UI theme";
    public string[] Aliases => new[] { "/theme", "themes" };

    public ThemeCommand() : this(new ThemeMemoryService())
    {
    }

    public ThemeCommand(ThemeMemoryService themeMemory)
    {
        _themeMemory = themeMemory;
    }

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
        {
            return Task.FromResult(ListThemes());
        }

        return Task.FromResult(SwitchTheme(args[0]));
    }

    private CommandResult ListThemes()
    {
        var result = new StringBuilder();
        result.AppendLine("Available Themes");
        result.AppendLine("----------------");
        foreach (var name in Theme.AvailableThemes)
        {
            var marker = string.Equals(name, Theme.Current.Name, StringComparison.OrdinalIgnoreCase)
                ? " (current)"
                : "";
            result.AppendLine($"  {name}{marker}");
        }
        result.AppendLine();
        result.AppendLine("Usage: /theme <name>");
        return CommandResult.CreateSuccess(result.ToString());
    }

    private CommandResult SwitchTheme(string requested)
    {
        var theme = Theme.GetByName(requested);
        if (theme == null)
        {
            var available = string.Join(", ", Theme.AvailableThemes);
            return CommandResult.Failure(
                $"Unknown theme: '{requested}'. Available themes: {available}");
        }

        Theme.Current = theme;
        _themeMemory.SaveTheme(theme.Name);
        return CommandResult.CreateSuccess($"Theme switched to '{theme.Name}'.");
    }
}
