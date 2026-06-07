using System;
using System.IO;
using System.Text.Json;

namespace Andy.Cli.Services;

/// <summary>
/// Service for remembering the user's selected UI theme across sessions.
/// </summary>
public class ThemeMemoryService
{
    private readonly string _configPath;

    /// <summary>
    /// Create a service that persists the selected theme to the default user
    /// config location (~/.andy/theme-memory.json).
    /// </summary>
    public ThemeMemoryService()
        : this(DefaultConfigPath())
    {
    }

    /// <summary>
    /// Create a service that persists the selected theme to the given file path.
    /// Primarily intended for testing against a temporary location.
    /// </summary>
    public ThemeMemoryService(string configPath)
    {
        _configPath = configPath;
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string DefaultConfigPath()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".andy");
        Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, "theme-memory.json");
    }

    /// <summary>
    /// Persist the selected theme name, preserving the saved transparent-background
    /// preference.
    /// </summary>
    public void SaveTheme(string themeName) => SaveTheme(themeName, LoadTransparentBackground());

    /// <summary>
    /// Persist the selected theme name together with the transparent-background
    /// preference.
    /// </summary>
    public void SaveTheme(string themeName, bool transparentBackground)
    {
        try
        {
            var memory = new ThemeMemory
            {
                Theme = themeName,
                TransparentBackground = transparentBackground,
                LastUsed = DateTime.UtcNow
            };
            var json = JsonSerializer.Serialize(memory, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_configPath, json);
        }
        catch
        {
            // Silently fail if we can't save
        }
    }

    /// <summary>
    /// Persist only the transparent-background preference, keeping the saved theme name.
    /// </summary>
    public void SaveTransparentBackground(bool transparentBackground)
        => SaveTheme(LoadTheme() ?? "", transparentBackground);

    /// <summary>
    /// Load the previously selected theme name, or null when none was saved.
    /// </summary>
    public string? LoadTheme()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var memory = JsonSerializer.Deserialize<ThemeMemory>(json);
                return string.IsNullOrWhiteSpace(memory?.Theme) ? null : memory!.Theme;
            }
        }
        catch
        {
            // If loading fails, behave as if nothing was saved
        }
        return null;
    }

    /// <summary>
    /// Load the persisted transparent-background preference (false when none saved).
    /// </summary>
    public bool LoadTransparentBackground()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var memory = JsonSerializer.Deserialize<ThemeMemory>(json);
                return memory?.TransparentBackground ?? false;
            }
        }
        catch
        {
            // If loading fails, behave as if nothing was saved
        }
        return false;
    }

    private class ThemeMemory
    {
        public string Theme { get; set; } = "";
        public bool TransparentBackground { get; set; }
        public DateTime LastUsed { get; set; }
    }
}
