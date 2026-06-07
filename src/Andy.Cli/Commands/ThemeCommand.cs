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
    public string Description => "List, switch, or toggle transparency of the UI theme";
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

        var first = args[0].Trim().ToLowerInvariant();
        if (first is "transparent" or "transparency")
        {
            return Task.FromResult(ToggleTransparent(args.Length > 1 ? args[1] : null));
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
            var theme = Theme.GetByName(name);
            var isCurrent = string.Equals(name, Theme.Current.Name, StringComparison.OrdinalIgnoreCase);
            var marker = isCurrent ? " (current)" : "";
            var transparentTag = theme is { OffersTransparentBackground: true } ? "  [offers transparent bg]" : "";
            result.AppendLine($"  {name}{marker}{transparentTag}");
        }
        result.AppendLine();

        // Transparent-background "check" for the active theme.
        var current = Theme.Current;
        if (current.OffersTransparentBackground)
        {
            var check = current.HasTransparentBackground ? "[x]" : "[ ]";
            result.AppendLine($"Transparent background: {check} {(current.HasTransparentBackground ? "on" : "off")}");
            result.AppendLine("Toggle with: /theme transparent on|off");
        }
        else
        {
            result.AppendLine("Transparent background: not available for this theme");
        }
        result.AppendLine();
        result.AppendLine("Usage: /theme <name>");
        return CommandResult.CreateSuccess(result.ToString());
    }

    private CommandResult SwitchTheme(string requested)
    {
        var baseTheme = Theme.GetByName(requested);
        if (baseTheme == null)
        {
            var available = string.Join(", ", Theme.AvailableThemes);
            return CommandResult.Failure(
                $"Unknown theme: '{requested}'. Available themes: {available}");
        }

        // Carry the user's transparent-background preference onto the new theme
        // (only applied when the new theme offers it).
        var transparentPref = _themeMemory.LoadTransparentBackground();
        var applied = Theme.Resolve(baseTheme.Name, transparentPref) ?? baseTheme;
        Theme.Current = applied;
        _themeMemory.SaveTheme(applied.Name);

        var note = applied.HasTransparentBackground ? " (transparent background)" : "";
        return CommandResult.CreateSuccess($"Theme switched to '{applied.Name}'.{note}");
    }

    private CommandResult ToggleTransparent(string? value)
    {
        var baseTheme = Theme.GetByName(Theme.Current.Name) ?? Theme.Dark;
        if (!baseTheme.OffersTransparentBackground)
        {
            return CommandResult.Failure(
                $"Theme '{baseTheme.Name}' does not offer a transparent background.");
        }

        bool enable = value?.Trim().ToLowerInvariant() switch
        {
            "on" or "true" or "yes" or "1" or "enable" or "enabled" => true,
            "off" or "false" or "no" or "0" or "disable" or "disabled" => false,
            _ => !Theme.Current.HasTransparentBackground // no/blank arg: toggle current state
        };

        Theme.Current = enable ? baseTheme.WithTransparentBackground() : baseTheme;
        _themeMemory.SaveTransparentBackground(enable);

        var check = enable ? "[x]" : "[ ]";
        return CommandResult.CreateSuccess(
            $"Transparent background {check} {(enable ? "enabled" : "disabled")} for '{baseTheme.Name}'.");
    }
}
