using System;
using Andy.Cli.Services;
using Andy.Cli.Widgets.Tools;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Configuration;

/// <summary>
/// Pushes the loaded configuration into the places that own a setting.
///
/// It lives here rather than inline in Program.Main for one reason: every entry
/// point (interactive, one-shot command, headless, ACP) needs the same handful of
/// lines, and duplicating them is exactly how the modes drift apart. Program.cs
/// calls <see cref="Apply"/> once and is done.
/// </summary>
public static class ConfigStartup
{
    /// <summary>
    /// Applies the settings that live in process-wide state rather than in DI:
    /// the diff layout preference and the agent-loop turn cap.
    /// </summary>
    public static void Apply(EffectiveConfiguration effective)
    {
        ArgumentNullException.ThrowIfNull(effective);

        DiffViewOptions.Style = effective.Config.Ui.DiffStyle switch
        {
            "unified" => DiffLayout.Unified,
            "split" => DiffLayout.Split,
            _ => DiffLayout.Auto,
        };

        SimpleAssistantService.SetMaxAgentTurns(effective.Config.Session.MaxTurns);
    }

    /// <summary>
    /// The theme to render with, and whether the background is transparent.
    ///
    /// <paramref name="memory"/> holds what the user last picked with <c>/theme</c>,
    /// which normally wins because it is the most recent deliberate act. A theme
    /// that was DECLARED - in a config file, ANDY_THEME, or <c>--theme</c> - wins
    /// over it instead, because otherwise a project could never pin its own theme
    /// and <c>--theme</c> would silently do nothing.
    /// </summary>
    public static (string? Theme, bool TransparentBackground) ResolveTheme(
        EffectiveConfiguration effective,
        ThemeMemoryService memory)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(memory);

        var declaredTheme = IsDeclared(effective, "ui.theme");
        var declaredTransparent = IsDeclared(effective, "ui.transparentBackground");

        var theme = declaredTheme
            ? effective.Config.Ui.Theme
            : memory.LoadTheme() ?? effective.Config.Ui.Theme;

        var transparent = declaredTransparent
            ? effective.Config.Ui.TransparentBackground
            : memory.LoadTransparentBackground();

        return (theme, transparent);
    }

    /// <summary>Configures logging from <c>logging.level</c> and <c>logging.console</c>.</summary>
    public static void ConfigureLogging(ILoggingBuilder builder, EffectiveConfiguration effective)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(effective);

        var level = ParseLevel(effective.Config.Logging.Level);
        builder.SetMinimumLevel(level);

        // Console logging is off unless asked for: anything written to stdout while
        // the TUI owns the terminal corrupts the frame.
        if (effective.Config.Logging.Console && level != LogLevel.None)
        {
            builder.AddConsole();
        }
    }

    internal static LogLevel ParseLevel(string? level) => (level ?? string.Empty).ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "information" or "info" => LogLevel.Information,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" => LogLevel.Critical,
        "none" or "off" => LogLevel.None,
        _ => LogLevel.Warning,
    };

    /// <summary>True when something above the packaged defaults set this key.</summary>
    private static bool IsDeclared(EffectiveConfiguration effective, string keyPath) =>
        effective.OriginOf(keyPath) is { } origin
        && origin.Source.Kind != ConfigSourceKind.PackagedDefaults;
}
