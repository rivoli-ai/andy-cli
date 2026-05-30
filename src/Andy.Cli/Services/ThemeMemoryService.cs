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
    /// Persist the selected theme name.
    /// </summary>
    public void SaveTheme(string themeName)
    {
        try
        {
            var memory = new ThemeMemory
            {
                Theme = themeName,
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

    private class ThemeMemory
    {
        public string Theme { get; set; } = "";
        public DateTime LastUsed { get; set; }
    }
}
