using System;
using System.IO;
using System.Linq;
using Andy.Cli.Configuration;
using Andy.Cli.Mcp;
using Andy.Cli.Services;
using Andy.Llm.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// The configuration is only worth having if the rest of the CLI actually reads it.
/// These tests take the effective configuration and push it through the same
/// adapters Program.cs uses: the MCP loader, Andy.Llm's options, the theme
/// resolution, and the packaged appsettings.json fold-in.
/// </summary>
public class ConfigIntegrationTests
{
    [Fact]
    public void McpServers_FromAndyJsonc_ReachTheMcpLoader()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "mcp": {
                "servers": {
                  "files": {
                    "transport": "stdio",
                    "command": "npx",
                    "args": ["-y", "@modelcontextprotocol/server-filesystem", "."]
                  }
                }
              }
            }
            """);

        var effective = workspace.Load();
        var result = McpConfigurationLoader.Load(
            applicationConfiguration: null,
            projectDirectory: workspace.WorkspaceDirectory,
            layeredConfiguration: effective.Config);

        var server = Assert.Single(result.Servers);
        Assert.Equal("files", server.Name);
        Assert.Equal("stdio", server.Transport);
        Assert.Equal("npx", server.Command);
        Assert.Equal(3, server.Args.Count);
        Assert.Contains(result.Sources, s => s.Contains(ConfigSchema.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public void DedicatedMcpProjectFile_StillWinsOverAndyJsonc()
    {
        // .andy/mcp-servers.json is the pre-existing location. Nothing that worked
        // before is allowed to start losing to the new file.
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            { "mcp": { "servers": { "files": { "transport": "stdio", "command": "from-andy-jsonc" } } } }
            """);
        File.WriteAllText(
            Path.Combine(workspace.WorkspaceDirectory, ".andy", "mcp-servers.json"),
            """
            { "servers": { "files": { "transport": "stdio", "command": "from-mcp-servers-json" } } }
            """);

        var result = McpConfigurationLoader.Load(
            applicationConfiguration: null,
            projectDirectory: workspace.WorkspaceDirectory,
            layeredConfiguration: workspace.Load().Config);

        var server = Assert.Single(result.Servers);
        Assert.Equal("from-mcp-servers-json", server.Command);
    }

    [Fact]
    public void McpLoader_KeepsItsOriginalTwoArgumentBehaviour()
    {
        using var workspace = new ConfigTestWorkspace();

        var result = McpConfigurationLoader.Load(null, workspace.WorkspaceDirectory);

        Assert.Empty(result.Servers);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void LlmOptionsBinder_OverridesOnlyWhatWasDeclared()
    {
        var options = new LlmOptions
        {
            DefaultProvider = "cerebras",
            Providers =
            {
                ["openai"] = new ProviderConfig
                {
                    ApiKey = "from-environment",
                    ApiBase = "https://api.openai.com/v1",
                    Model = "gpt-4o",
                },
            },
        };

        LlmOptionsBinder.Apply(options, new LlmSection
        {
            DefaultProvider = "openai",
            Providers = { ["openai"] = new LlmProviderSection { Model = "gpt-5.2-codex" } },
        });

        Assert.Equal("openai", options.DefaultProvider);
        Assert.Equal("gpt-5.2-codex", options.Providers["openai"].Model);
        // Not declared, so the credential the environment supplied survives.
        Assert.Equal("from-environment", options.Providers["openai"].ApiKey);
        Assert.Equal("https://api.openai.com/v1", options.Providers["openai"].ApiBase);
    }

    [Fact]
    public void LlmOptionsBinder_AddsProvidersThatDidNotExistYet()
    {
        var options = new LlmOptions();

        LlmOptionsBinder.Apply(options, new LlmSection
        {
            Providers =
            {
                ["work-proxy"] = new LlmProviderSection
                {
                    Provider = "openai",
                    ApiBase = "https://proxy.example.test/v1",
                    Model = "gpt-4o",
                },
            },
        });

        Assert.Equal("openai", options.Providers["work-proxy"].Provider);
        Assert.Equal("https://proxy.example.test/v1", options.Providers["work-proxy"].ApiBase);
    }

    [Fact]
    public void PackagedAppSettings_IsFoldedIntoTheDefaultsLayer()
    {
        using var workspace = new ConfigTestWorkspace();
        var appSettings = Path.Combine(workspace.WorkspaceDirectory, "appsettings.json");
        // Deliberately in the PascalCase shape the existing packaged file uses.
        File.WriteAllText(appSettings, """
            {
              "Llm": {
                "DefaultProvider": "",
                "Providers": {
                  "openai/codex": {
                    "Provider": "openai",
                    "ApiBase": "https://api.openai.com/v1",
                    "Model": "gpt-5-codex",
                    "Enabled": true
                  }
                }
              }
            }
            """);

        var effective = new AndyConfigurationService().Load(new ConfigLoadRequest
        {
            WorkspaceDirectory = workspace.WorkspaceDirectory,
            UserHomeDirectory = workspace.HomeDirectory,
            AppSettingsPath = appSettings,
            EnvironmentOverride = workspace.Environment,
        });

        Assert.False(effective.HasErrors, string.Join("; ", effective.Errors));
        var provider = effective.Config.Llm.Providers["openai/codex"];
        Assert.Equal("openai", provider.Provider);
        Assert.Equal("gpt-5-codex", provider.Model);
        // "DefaultProvider": "" means "not set", not "the empty provider".
        Assert.Null(effective.Config.Llm.DefaultProvider);
    }

    [Fact]
    public void AndyJsonc_OverridesThePackagedAppSettings()
    {
        using var workspace = new ConfigTestWorkspace();
        var appSettings = Path.Combine(workspace.WorkspaceDirectory, "appsettings.json");
        File.WriteAllText(appSettings, """
            { "Llm": { "Providers": { "openai": { "Model": "gpt-4o" } } } }
            """);
        workspace.WriteProject("""
            { "llm": { "providers": { "openai": { "model": "gpt-5.2-codex" } } } }
            """);

        var effective = new AndyConfigurationService().Load(new ConfigLoadRequest
        {
            WorkspaceDirectory = workspace.WorkspaceDirectory,
            UserHomeDirectory = workspace.HomeDirectory,
            AppSettingsPath = appSettings,
            EnvironmentOverride = workspace.Environment,
        });

        Assert.Equal("gpt-5.2-codex", effective.Config.Llm.Providers["openai"].Model);
        Assert.Equal(
            ConfigSourceKind.Project,
            effective.OriginOf("llm.providers.openai.model")!.Source.Kind);
    }

    [Fact]
    public void Theme_SavedWithSlashTheme_WinsOverThePackagedDefaultOnly()
    {
        using var workspace = new ConfigTestWorkspace();
        var memory = new ThemeMemoryService(
            Path.Combine(workspace.HomeDirectory, ".andy", "theme-memory.json"));
        memory.SaveTheme("dracula");

        // Nothing declares ui.theme, so the last /theme choice stands.
        var (theme, _) = ConfigStartup.ResolveTheme(workspace.Load(), memory);
        Assert.Equal("dracula", theme);

        // A project that pins a theme must be able to override that choice.
        workspace.WriteProject("""{ "ui": { "theme": "nord" } }""");
        (theme, _) = ConfigStartup.ResolveTheme(workspace.Load(), memory);
        Assert.Equal("nord", theme);

        // And --theme must override the project.
        workspace.WithArguments("--theme", "light");
        (theme, _) = ConfigStartup.ResolveTheme(workspace.Load(), memory);
        Assert.Equal("light", theme);
    }

    [Fact]
    public void TransparentBackground_FollowsTheSameRule()
    {
        using var workspace = new ConfigTestWorkspace();
        var memory = new ThemeMemoryService(
            Path.Combine(workspace.HomeDirectory, ".andy", "theme-memory.json"));
        memory.SaveTheme("dracula", transparentBackground: true);

        var (_, transparent) = ConfigStartup.ResolveTheme(workspace.Load(), memory);
        Assert.True(transparent);

        workspace.WriteProject("""{ "ui": { "transparentBackground": false } }""");
        (_, transparent) = ConfigStartup.ResolveTheme(workspace.Load(), memory);
        Assert.False(transparent);
    }

    [Theory]
    [InlineData("trace", Microsoft.Extensions.Logging.LogLevel.Trace)]
    [InlineData("debug", Microsoft.Extensions.Logging.LogLevel.Debug)]
    [InlineData("information", Microsoft.Extensions.Logging.LogLevel.Information)]
    [InlineData("warning", Microsoft.Extensions.Logging.LogLevel.Warning)]
    [InlineData("error", Microsoft.Extensions.Logging.LogLevel.Error)]
    [InlineData("critical", Microsoft.Extensions.Logging.LogLevel.Critical)]
    [InlineData("none", Microsoft.Extensions.Logging.LogLevel.None)]
    [InlineData("nonsense", Microsoft.Extensions.Logging.LogLevel.Warning)]
    public void LoggingLevels_MapOntoTheFrameworkLevels(
        string level,
        Microsoft.Extensions.Logging.LogLevel expected)
    {
        Assert.Equal(expected, ConfigStartup.ParseLevel(level));
    }

    [Fact]
    public void SessionDirectory_FromConfig_IsWhatTheSessionStoreUses()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "session": { "directory": "saved" } }""");

        var effective = workspace.Load();
        var store = new Andy.Cli.Services.Sessions.SessionStore(effective.Config.Session.Directory);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.WorkspaceDirectory, "saved")),
            store.DirectoryPath);
    }

    [Fact]
    public void SharedInstance_IsLoadedOnce()
    {
        using var workspace = new ConfigTestWorkspace();
        try
        {
            var first = AndyConfigurationService.InitializeShared(workspace.Request(), force: true);
            var second = AndyConfigurationService.InitializeShared(new ConfigLoadRequest());

            Assert.Same(first, second);
            Assert.Same(first, AndyConfigurationService.Shared);
        }
        finally
        {
            // Leaving a temp workspace cached would leak into other tests.
            typeof(AndyConfigurationService)
                .GetMethod("ResetShared", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, null);
        }
    }
}
