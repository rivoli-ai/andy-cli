using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Andy.Cli.Services;

/// <summary>
/// Service for remembering the last used model for each provider
/// </summary>
public class ModelMemoryService
{
    private readonly string _configPath;
    private Dictionary<string, ModelMemory> _memory;

    public ModelMemoryService()
    {
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".andy");
        Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "model-memory.json");
        _memory = LoadMemory();
    }

    /// <summary>
    /// Remember the model for a provider
    /// </summary>
    public void RememberModel(string provider, string model)
    {
        _memory[provider] = new ModelMemory
        {
            Provider = provider,
            Model = model,
            LastUsed = DateTime.UtcNow
        };
        SaveMemory();
    }

    /// <summary>
    /// Get the last used model for a provider
    /// </summary>
    public string? GetLastModel(string provider)
    {
        return _memory.TryGetValue(provider, out var memory) ? memory.Model : null;
    }

    /// <summary>
    /// Get the current provider and model
    /// </summary>
    public (string Provider, string Model)? GetCurrent()
    {
        if (_memory.TryGetValue("_current", out var current))
        {
            return (current.Provider, current.Model);
        }
        return null;
    }

    /// <summary>
    /// Set the current provider and model
    /// </summary>
    public void SetCurrent(string provider, string model)
    {
        _memory["_current"] = new ModelMemory
        {
            Provider = provider,
            Model = model,
            LastUsed = DateTime.UtcNow
        };
        RememberModel(provider, model);
    }

    /// <summary>
    /// Get all remembered models
    /// </summary>
    public Dictionary<string, string> GetAllModels()
    {
        var result = new Dictionary<string, string>();
        foreach (var kvp in _memory)
        {
            if (kvp.Key != "_current")
            {
                result[kvp.Key] = kvp.Value.Model;
            }
        }
        return result;
    }

    private Dictionary<string, ModelMemory> LoadMemory()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<Dictionary<string, ModelMemory>>(json)
                    ?? new Dictionary<string, ModelMemory>();
            }
        }
        catch
        {
            // If loading fails, start fresh
        }
        return new Dictionary<string, ModelMemory>();
    }

    private void SaveMemory()
    {
        try
        {
            var json = JsonSerializer.Serialize(_memory, new JsonSerializerOptions
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

    private class ModelMemory
    {
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public DateTime LastUsed { get; set; }
    }
}