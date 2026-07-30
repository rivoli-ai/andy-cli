using System;
using System.Collections.Generic;
using System.IO;
using Andy.Cli.Configuration;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// A throwaway home + workspace pair for configuration tests.
///
/// Every load is fully isolated: the packaged appsettings.json is switched off
/// (empty path), the environment is an explicit dictionary rather than the real
/// process environment, and both scopes live under a fresh temp directory. That is
/// what makes the precedence assertions mean something - nothing can leak in from
/// the machine running the suite.
/// </summary>
public sealed class ConfigTestWorkspace : IDisposable
{
    private readonly string _root;

    public ConfigTestWorkspace()
    {
        _root = Path.Combine(Path.GetTempPath(), "andy-config-tests", Guid.NewGuid().ToString("N"));
        HomeDirectory = Path.Combine(_root, "home");
        WorkspaceDirectory = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(HomeDirectory, ".andy"));
        Directory.CreateDirectory(Path.Combine(WorkspaceDirectory, ".andy"));
    }

    public string HomeDirectory { get; }

    public string WorkspaceDirectory { get; }

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    public List<string> CommandLine { get; } = new();

    public string UserConfigPath => Path.Combine(HomeDirectory, ".andy", ConfigSchema.FileName);

    public string ProjectConfigPath => Path.Combine(WorkspaceDirectory, ConfigSchema.FileName);

    public string ProjectDotAndyConfigPath =>
        Path.Combine(WorkspaceDirectory, ".andy", ConfigSchema.FileName);

    public ConfigTestWorkspace WriteUser(string jsonc)
    {
        File.WriteAllText(UserConfigPath, jsonc);
        return this;
    }

    public ConfigTestWorkspace WriteProject(string jsonc)
    {
        File.WriteAllText(ProjectConfigPath, jsonc);
        return this;
    }

    public ConfigTestWorkspace WriteProjectDotAndy(string jsonc)
    {
        File.WriteAllText(ProjectDotAndyConfigPath, jsonc);
        return this;
    }

    public ConfigTestWorkspace WithEnvironment(string name, string value)
    {
        Environment[name] = value;
        return this;
    }

    public ConfigTestWorkspace WithArguments(params string[] args)
    {
        CommandLine.AddRange(args);
        return this;
    }

    public ConfigLoadRequest Request() => new()
    {
        WorkspaceDirectory = WorkspaceDirectory,
        UserHomeDirectory = HomeDirectory,
        AppSettingsPath = string.Empty,
        CommandLineArguments = CommandLine.ToArray(),
        EnvironmentOverride = Environment,
    };

    public EffectiveConfiguration Load() => new AndyConfigurationService().Load(Request());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }
}
