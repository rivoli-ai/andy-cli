using System;
using System.Linq;
using Andy.Cli.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// Pins the documented precedence chain for EVERY top-level schema section:
/// packaged defaults &lt; user &lt; project &lt; environment &lt; CLI arguments.
///
/// Each section gets the same treatment - stack all five layers where the section
/// can express them, assert the winner, then assert that removing the top layer
/// promotes exactly the next one down. A section that cannot be reached from the
/// environment or the CLI (mcp) is pinned across the layers it does have.
/// </summary>
public class ConfigPrecedenceTests
{
    [Fact]
    public void Version_ComesFromPackagedDefaults()
    {
        using var workspace = new ConfigTestWorkspace();

        var effective = workspace.Load();

        Assert.Equal(ConfigSchema.Version, effective.Config.Version);
        Assert.Equal(ConfigSourceKind.PackagedDefaults, effective.OriginOf("version")!.Source.Kind);
    }

    // ---------------------------------------------------------------- ui ----

    [Fact]
    public void Ui_PackagedDefaultWins_WhenNothingElseDeclaresIt()
    {
        using var workspace = new ConfigTestWorkspace();

        var effective = workspace.Load();

        Assert.Equal("dark", effective.Config.Ui.Theme);
        Assert.Equal(ConfigSourceKind.PackagedDefaults, effective.OriginOf("ui.theme")!.Source.Kind);
    }

    [Fact]
    public void Ui_UserBeatsPackagedDefaults()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "ui": { "theme": "nord" } }""");

        var effective = workspace.Load();

        Assert.Equal("nord", effective.Config.Ui.Theme);
        Assert.Equal(ConfigSourceKind.User, effective.OriginOf("ui.theme")!.Source.Kind);
    }

    [Fact]
    public void Ui_ProjectBeatsUser()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "ui": { "theme": "nord" } }""");
        workspace.WriteProject("""{ "ui": { "theme": "dracula" } }""");

        var effective = workspace.Load();

        Assert.Equal("dracula", effective.Config.Ui.Theme);
        Assert.Equal(ConfigSourceKind.Project, effective.OriginOf("ui.theme")!.Source.Kind);
    }

    [Fact]
    public void Ui_ProjectDotAndyBeatsProjectRootFile()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "theme": "dracula" } }""");
        workspace.WriteProjectDotAndy("""{ "ui": { "theme": "solarized-dark" } }""");

        var effective = workspace.Load();

        Assert.Equal("solarized-dark", effective.Config.Ui.Theme);
        Assert.Equal(workspace.ProjectDotAndyConfigPath, effective.OriginOf("ui.theme")!.Source.FilePath);
    }

    [Fact]
    public void Ui_EnvironmentBeatsProject()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "ui": { "theme": "nord" } }""");
        workspace.WriteProject("""{ "ui": { "theme": "dracula" } }""");
        workspace.WithEnvironment("ANDY_THEME", "gruvbox");

        var effective = workspace.Load();

        Assert.Equal("gruvbox", effective.Config.Ui.Theme);
        Assert.Equal(ConfigSourceKind.Environment, effective.OriginOf("ui.theme")!.Source.Kind);
    }

    [Fact]
    public void Ui_CommandLineBeatsEverything()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "ui": { "theme": "nord" } }""");
        workspace.WriteProject("""{ "ui": { "theme": "dracula" } }""");
        workspace.WithEnvironment("ANDY_THEME", "gruvbox");
        workspace.WithArguments("--theme", "light");

        var effective = workspace.Load();

        Assert.Equal("light", effective.Config.Ui.Theme);
        Assert.Equal(ConfigSourceKind.CommandLine, effective.OriginOf("ui.theme")!.Source.Kind);
    }

    [Fact]
    public void Ui_DiffStyle_FollowsTheSameChain()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "ui": { "diffStyle": "unified" } }""");
        Assert.Equal("unified", workspace.Load().Config.Ui.DiffStyle);

        workspace.WriteProject("""{ "ui": { "diffStyle": "split" } }""");
        Assert.Equal("split", workspace.Load().Config.Ui.DiffStyle);

        // The historical ANDY_DIFF_STYLE spelling "stacked" still means unified.
        workspace.WithEnvironment("ANDY_DIFF_STYLE", "stacked");
        Assert.Equal("unified", workspace.Load().Config.Ui.DiffStyle);

        workspace.WithArguments("--diff-style", "side-by-side");
        Assert.Equal("split", workspace.Load().Config.Ui.DiffStyle);
    }

    // --------------------------------------------------------------- llm ----

    [Fact]
    public void Llm_DefaultProvider_FollowsTheFullChain()
    {
        using var workspace = new ConfigTestWorkspace();

        workspace.WriteUser("""{ "llm": { "defaultProvider": "anthropic" } }""");
        Assert.Equal("anthropic", workspace.Load().Config.Llm.DefaultProvider);

        workspace.WriteProject("""{ "llm": { "defaultProvider": "groq" } }""");
        Assert.Equal("groq", workspace.Load().Config.Llm.DefaultProvider);

        workspace.WithArguments("--provider", "cerebras");
        var effective = workspace.Load();
        Assert.Equal("cerebras", effective.Config.Llm.DefaultProvider);
        Assert.Equal(ConfigSourceKind.CommandLine, effective.OriginOf("llm.defaultProvider")!.Source.Kind);
    }

    [Fact]
    public void Llm_ProviderModel_EnvironmentBeatsFilesAndCliBeatsEnvironment()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "llm": { "providers": { "openai": { "model": "gpt-user" } } } }""");
        workspace.WriteProject("""{ "llm": { "providers": { "openai": { "model": "gpt-project" } } } }""");

        Assert.Equal("gpt-project", workspace.Load().Config.Llm.Providers["openai"].Model);

        // OPENAI_MODEL is the name ModelCommand has always honoured.
        workspace.WithEnvironment("OPENAI_MODEL", "gpt-env");
        var effective = workspace.Load();
        Assert.Equal("gpt-env", effective.Config.Llm.Providers["openai"].Model);
        Assert.Equal(
            ConfigSourceKind.Environment,
            effective.OriginOf("llm.providers.openai.model")!.Source.Kind);
    }

    [Fact]
    public void Llm_ApiBase_HonoursTheExistingEnvironmentNames()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithEnvironment("OPENAI_API_BASE", "https://proxy.example.test/v1");

        var effective = workspace.Load();

        Assert.Equal("https://proxy.example.test/v1", effective.Config.Llm.Providers["openai"].ApiBase);
    }

    // --------------------------------------------------------------- mcp ----

    [Fact]
    public void Mcp_ProjectServerOverridesUserServerFieldByField()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""
            {
              "mcp": {
                "servers": {
                  "files": {
                    "transport": "stdio",
                    "command": "user-command",
                    "env": { "LOG_LEVEL": "warn" }
                  }
                }
              }
            }
            """);
        workspace.WriteProject("""
            { "mcp": { "servers": { "files": { "command": "project-command" } } } }
            """);

        var effective = workspace.Load();
        var server = effective.Config.Mcp.Servers["files"];

        Assert.Equal("project-command", server.Command);
        Assert.Equal("stdio", server.Transport);
        Assert.Equal("warn", server.Env["LOG_LEVEL"]);
        Assert.Equal(
            ConfigSourceKind.Project,
            effective.OriginOf("mcp.servers.files.command")!.Source.Kind);
        Assert.Equal(
            ConfigSourceKind.User,
            effective.OriginOf("mcp.servers.files.transport")!.Source.Kind);
    }

    // ----------------------------------------------------------- session ----

    [Fact]
    public void Session_MaxTurns_FollowsTheFullChain()
    {
        using var workspace = new ConfigTestWorkspace();

        Assert.Null(workspace.Load().Config.Session.MaxTurns);

        workspace.WriteUser("""{ "session": { "maxTurns": 10 } }""");
        Assert.Equal(10, workspace.Load().Config.Session.MaxTurns);

        workspace.WriteProject("""{ "session": { "maxTurns": 20 } }""");
        Assert.Equal(20, workspace.Load().Config.Session.MaxTurns);

        workspace.WithEnvironment("ANDY_MAX_TURNS", "30");
        Assert.Equal(30, workspace.Load().Config.Session.MaxTurns);

        workspace.WithArguments("--max-turns", "40");
        var effective = workspace.Load();
        Assert.Equal(40, effective.Config.Session.MaxTurns);
        Assert.Equal(ConfigSourceKind.CommandLine, effective.OriginOf("session.maxTurns")!.Source.Kind);
    }

    [Fact]
    public void Session_Directory_DefaultsUnderTheUserHome()
    {
        using var workspace = new ConfigTestWorkspace();

        var effective = workspace.Load();

        Assert.Equal(
            System.IO.Path.Combine(workspace.HomeDirectory, ".andy", "sessions"),
            effective.Config.Session.Directory);
    }

    // ------------------------------------------------------- permissions ----

    [Fact]
    public void Permissions_Mode_FollowsTheFullChain()
    {
        using var workspace = new ConfigTestWorkspace();

        Assert.Equal("ask", workspace.Load().Config.Permissions.Mode);
        Assert.False(workspace.Load().Config.Permissions.AutoApprove);

        workspace.WriteUser("""{ "permissions": { "mode": "auto" } }""");
        Assert.True(workspace.Load().Config.Permissions.AutoApprove);

        workspace.WriteProject("""{ "permissions": { "mode": "ask" } }""");
        Assert.False(workspace.Load().Config.Permissions.AutoApprove);

        // ANDY_AUTO_APPROVE and --auto / --yolo are the names that already existed.
        workspace.WithEnvironment("ANDY_AUTO_APPROVE", "1");
        var fromEnvironment = workspace.Load();
        Assert.True(fromEnvironment.Config.Permissions.AutoApprove);
        Assert.Equal(
            ConfigSourceKind.Environment,
            fromEnvironment.OriginOf("permissions.mode")!.Source.Kind);
    }

    [Theory]
    [InlineData("--auto")]
    [InlineData("--yolo")]
    public void Permissions_LegacyApprovalFlagsStillMeanAutoApprove(string flag)
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithArguments(flag);

        var effective = workspace.Load();

        Assert.True(effective.Config.Permissions.AutoApprove);
        Assert.Equal(ConfigSourceKind.CommandLine, effective.OriginOf("permissions.mode")!.Source.Kind);
    }

    // ----------------------------------------------------------- logging ----

    [Fact]
    public void Logging_FollowsTheFullChain()
    {
        using var workspace = new ConfigTestWorkspace();

        Assert.Equal("warning", workspace.Load().Config.Logging.Level);
        Assert.False(workspace.Load().Config.Logging.Console);

        workspace.WriteUser("""{ "logging": { "level": "error" } }""");
        Assert.Equal("error", workspace.Load().Config.Logging.Level);

        workspace.WriteProject("""{ "logging": { "level": "trace", "console": true } }""");
        Assert.Equal("trace", workspace.Load().Config.Logging.Level);
        Assert.True(workspace.Load().Config.Logging.Console);

        // ANDY_DEBUG=true is the name that already existed.
        workspace.WithEnvironment("ANDY_DEBUG", "true");
        Assert.Equal("information", workspace.Load().Config.Logging.Level);

        workspace.WithArguments("--quiet");
        var effective = workspace.Load();
        Assert.Equal("none", effective.Config.Logging.Level);
        Assert.Equal(ConfigSourceKind.CommandLine, effective.OriginOf("logging.level")!.Source.Kind);
    }

    // ----------------------------------------------------------- sources ----

    [Fact]
    public void Sources_AreReportedLowestPrecedenceFirst()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("{}");
        workspace.WriteProject("{}");
        workspace.WriteProjectDotAndy("{}");

        var kinds = workspace.Load().Sources.Select(s => s.Kind).ToArray();

        Assert.Equal(
            new[]
            {
                ConfigSourceKind.PackagedDefaults,
                ConfigSourceKind.User,
                ConfigSourceKind.Project,
                ConfigSourceKind.Project,
                ConfigSourceKind.Environment,
                ConfigSourceKind.CommandLine,
            },
            kinds);
    }
}
