using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Andy.Cli.Configuration;
using Andy.Cli.HeadlessConfig;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// A headless workspace folder is carried across several agentic sessions, so the
/// project andy.jsonc in that folder has to apply to headless runs
/// (rivoli-ai/andy-cli#280). These tests pin the resulting precedence and, more
/// importantly, the two things that must NOT follow from it: a checked-in file
/// cannot loosen a containerised run's permissions, and a run that needs to
/// reproduce from its own file alone can opt out.
/// </summary>
public class HeadlessLayeredConfigTests
{
    /// <summary>
    /// A minimal but schema-valid headless run config, written into
    /// <paramref name="workspace"/>. The overrides let a test change one field
    /// without restating the contract.
    /// </summary>
    private static (HeadlessRunConfig Config, string Path) WriteRunConfig(
        ConfigTestWorkspace workspace,
        string provider = "openai",
        string model = "gpt-4o",
        string? apiKeyRef = null,
        int maxIterations = 7)
    {
        var document = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["run_id"] = Guid.NewGuid().ToString(),
            ["agent"] = new Dictionary<string, object?>
            {
                ["slug"] = "test-agent",
                ["instructions"] = "Do the thing.",
            },
            ["model"] = apiKeyRef is null
                ? new Dictionary<string, object?> { ["provider"] = provider, ["id"] = model }
                : new Dictionary<string, object?>
                {
                    ["provider"] = provider,
                    ["id"] = model,
                    ["api_key_ref"] = apiKeyRef,
                },
            ["tools"] = Array.Empty<object>(),
            ["workspace"] = new Dictionary<string, object?>
            {
                ["root"] = workspace.WorkspaceDirectory,
            },
            ["output"] = new Dictionary<string, object?>
            {
                ["file"] = Path.Combine(workspace.WorkspaceDirectory, "output.json"),
                ["stream"] = "stdout",
            },
            ["limits"] = new Dictionary<string, object?>
            {
                ["max_iterations"] = maxIterations,
                ["timeout_seconds"] = 30,
            },
        };

        var path = Path.Combine(workspace.WorkspaceDirectory, "run-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        var load = HeadlessConfigLoader.TryLoadAsync(path).GetAwaiter().GetResult();
        Assert.True(load.IsSuccess, load.Error);
        return (load.Config!, path);
    }

    /// <summary>
    /// Loads exactly the way HeadlessRunner does: workspace rooted at
    /// <c>workspace.root</c>, the run config as the override layer.
    /// </summary>
    private static EffectiveConfiguration LoadAsHeadless(
        ConfigTestWorkspace workspace,
        HeadlessRunConfig runConfig,
        string runConfigPath,
        bool isolated = false) =>
        new AndyConfigurationService().Load(new ConfigLoadRequest
        {
            WorkspaceDirectory = workspace.WorkspaceDirectory,
            UserHomeDirectory = workspace.HomeDirectory,
            AppSettingsPath = string.Empty,
            EnvironmentOverride = workspace.Environment,
            IncludeUserAndProjectLayers = !isolated,
            OverrideLayer = HeadlessConfigLayer.Build(runConfig, runConfigPath),
        });

    // ------------------------------------------------------- it applies ----

    [Fact]
    public void ProjectAndyJsonc_AppliesToAHeadlessRun()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "llm": {
                "providers": {
                  "openai": { "apiBase": "https://gateway.corp.example/v1" }
                }
              },
              "logging": { "level": "error" }
            }
            """);
        var (runConfig, path) = WriteRunConfig(workspace);

        var effective = LoadAsHeadless(workspace, runConfig, path);

        Assert.False(effective.HasErrors, string.Join("; ", effective.Errors));
        Assert.Equal(
            "https://gateway.corp.example/v1",
            effective.Config.Llm.Providers["openai"].ApiBase);
        Assert.Equal("error", effective.Config.Logging.Level);
        Assert.Equal(
            ConfigSourceKind.Project,
            effective.OriginOf("llm.providers.openai.apiBase")!.Source.Kind);
    }

    [Fact]
    public void UserAndyJsonc_AppliesToo_AndProjectStillBeatsIt()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "logging": { "level": "trace" } }""");
        var (runConfig, path) = WriteRunConfig(workspace);

        Assert.Equal("trace", LoadAsHeadless(workspace, runConfig, path).Config.Logging.Level);

        workspace.WriteProject("""{ "logging": { "level": "critical" } }""");

        Assert.Equal("critical", LoadAsHeadless(workspace, runConfig, path).Config.Logging.Level);
    }

    [Fact]
    public void ProjectDiscovery_IsRootedAtWorkspaceRoot_NotTheProcessDirectory()
    {
        // The container's working directory is not the folder the operator
        // configured, so discovery has to follow workspace.root.
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "theme": "nord" } }""");
        var (runConfig, path) = WriteRunConfig(workspace);

        var effective = LoadAsHeadless(workspace, runConfig, path);

        Assert.Equal("nord", effective.Config.Ui.Theme);
        Assert.Equal(
            workspace.ProjectConfigPath,
            effective.OriginOf("ui.theme")!.Source.FilePath);
    }

    // ------------------------------------------------------- precedence ----

    [Fact]
    public void RunConfigFile_BeatsProjectAndyJsonc_KeyByKey()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "llm": {
                "defaultProvider": "anthropic",
                "defaultModel": "from-project",
                "providers": {
                  "openai": { "model": "from-project", "apiBase": "https://gateway.corp.example/v1" }
                }
              },
              "session": { "maxTurns": 999 }
            }
            """);
        var (runConfig, path) = WriteRunConfig(workspace, provider: "openai", model: "gpt-4o", maxIterations: 7);

        var effective = LoadAsHeadless(workspace, runConfig, path);

        // The run config wins where it speaks...
        Assert.Equal("openai", effective.Config.Llm.DefaultProvider);
        Assert.Equal("gpt-4o", effective.Config.Llm.DefaultModel);
        Assert.Equal("gpt-4o", effective.Config.Llm.Providers["openai"].Model);
        Assert.Equal(7, effective.Config.Session.MaxTurns);
        Assert.Equal(
            ConfigSourceKind.HeadlessConfig,
            effective.OriginOf("llm.defaultProvider")!.Source.Kind);

        // ...and the project keeps the fields it did not mention.
        Assert.Equal(
            "https://gateway.corp.example/v1",
            effective.Config.Llm.Providers["openai"].ApiBase);
        Assert.Equal(
            ConfigSourceKind.Project,
            effective.OriginOf("llm.providers.openai.apiBase")!.Source.Kind);
    }

    [Fact]
    public void RunConfigFile_SitsAboveTheEnvironment()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithEnvironment("OPENAI_MODEL", "from-environment");
        var (runConfig, path) = WriteRunConfig(workspace, provider: "openai", model: "gpt-4o");

        var effective = LoadAsHeadless(workspace, runConfig, path);

        Assert.Equal("gpt-4o", effective.Config.Llm.Providers["openai"].Model);
        Assert.Equal(
            ConfigSourceKind.HeadlessConfig,
            effective.OriginOf("llm.providers.openai.model")!.Source.Kind);
    }

    [Fact]
    public void Sources_AreOrderedPackagedUserProjectEnvironmentRunConfigCli()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("{}");
        workspace.WriteProject("{}");
        var (runConfig, path) = WriteRunConfig(workspace);

        var kinds = LoadAsHeadless(workspace, runConfig, path).Sources.Select(s => s.Kind).ToArray();

        Assert.Equal(
            new[]
            {
                ConfigSourceKind.PackagedDefaults,
                ConfigSourceKind.User,
                ConfigSourceKind.Project,
                ConfigSourceKind.Environment,
                ConfigSourceKind.HeadlessConfig,
                ConfigSourceKind.CommandLine,
            },
            kinds);
    }

    [Fact]
    public void ApiKeyRef_BecomesASubstitutedSecretAndIsNeverPrinted()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithEnvironment("RUN_SCOPED_KEY_280", "sk-headless-secret-280");
        var (runConfig, path) = WriteRunConfig(workspace, apiKeyRef: "env:RUN_SCOPED_KEY_280");

        var effective = LoadAsHeadless(workspace, runConfig, path);
        var output = ConfigReportFormatter.FormatEffective(effective, includeSources: true);

        Assert.Equal("sk-headless-secret-280", effective.Config.Llm.Providers["openai"].ApiKey);
        Assert.DoesNotContain("sk-headless-secret-280", output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------ permissions ----

    [Fact]
    public void ProjectAndyJsonc_CannotLoosenHeadlessPermissions()
    {
        // A committed file must never be able to turn a containerised run into an
        // auto-approving one.
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "permissions": { "mode": "auto" } }""");
        var (runConfig, path) = WriteRunConfig(workspace);

        var effective = LoadAsHeadless(workspace, runConfig, path);

        Assert.Equal("ask", effective.Config.Permissions.Mode);
        Assert.False(effective.Config.Permissions.AutoApprove);
        Assert.Equal(
            ConfigSourceKind.HeadlessConfig,
            effective.OriginOf("permissions.mode")!.Source.Kind);
    }

    [Fact]
    public void UserAndyJsoncAndEnvironment_CannotLoosenHeadlessPermissionsEither()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "permissions": { "mode": "auto" } }""");
        workspace.WithEnvironment("ANDY_AUTO_APPROVE", "1");
        var (runConfig, path) = WriteRunConfig(workspace);

        var effective = LoadAsHeadless(workspace, runConfig, path);

        Assert.False(effective.Config.Permissions.AutoApprove);
    }

    [Fact]
    public void HeadlessLayer_PinsPermissionModeRegardlessOfTheRunConfigsAllowList()
    {
        using var workspace = new ConfigTestWorkspace();
        var (runConfig, path) = WriteRunConfig(workspace);

        var layer = HeadlessConfigLayer.Build(runConfig with
        {
            Permissions = new HeadlessPermissions { AllowedTools = new[] { "write_file" } },
        }, path);

        Assert.Equal("ask", layer.Root["permissions"]!["mode"]!.GetValue<string>());
        // The allow-list is the run config's business and is not projected here:
        // the runtime reads it straight from HeadlessRunConfig.
        Assert.Null(layer.Root["permissions"]!["allowedTools"]);
    }

    // -------------------------------------------------------- isolation ----

    [Fact]
    public void Isolated_SkipsTheUserAndProjectLayers()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "logging": { "level": "trace" } }""");
        workspace.WriteProject("""
            { "llm": { "providers": { "openai": { "apiBase": "https://gateway.corp.example/v1" } } } }
            """);
        var (runConfig, path) = WriteRunConfig(workspace);

        var isolated = LoadAsHeadless(workspace, runConfig, path, isolated: true);

        Assert.DoesNotContain(isolated.Sources, s => s.Kind == ConfigSourceKind.User);
        Assert.DoesNotContain(isolated.Sources, s => s.Kind == ConfigSourceKind.Project);
        Assert.Equal("warning", isolated.Config.Logging.Level);
        Assert.NotEqual(
            "https://gateway.corp.example/v1",
            isolated.Config.Llm.Providers["openai"].ApiBase);
    }

    [Fact]
    public void Isolated_StillAppliesTheRunConfigAndTheEnvironment()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "logging": { "level": "trace" } }""");
        workspace.WithEnvironment("ANDY_DEBUG", "true");
        var (runConfig, path) = WriteRunConfig(workspace, provider: "openai", model: "gpt-4o");

        var isolated = LoadAsHeadless(workspace, runConfig, path, isolated: true);

        Assert.Equal("gpt-4o", isolated.Config.Llm.DefaultModel);
        Assert.Equal("information", isolated.Config.Logging.Level);
    }

    [Fact]
    public void Isolated_SurvivesABrokenProjectFile()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("{ this is not json");
        var (runConfig, path) = WriteRunConfig(workspace);

        Assert.False(LoadAsHeadless(workspace, runConfig, path, isolated: true).HasErrors);
        Assert.True(LoadAsHeadless(workspace, runConfig, path).HasErrors);
    }

    // ------------------------------------------------------ diagnostics ----

    [Fact]
    public void BrokenProjectFile_IsReportedWithSourceLineColumnAndKeyPath()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "ui": { "theme": "nord" },
              "formatter": { "onSave": true }
            }
            """);
        var (runConfig, path) = WriteRunConfig(workspace);

        var effective = LoadAsHeadless(workspace, runConfig, path);
        var diagnostic = Assert.Single(
            effective.Errors, d => d.Code == ConfigDiagnosticCodes.UnknownKey);

        Assert.Equal(workspace.ProjectConfigPath, diagnostic.Source.FilePath);
        Assert.Equal("formatter", diagnostic.KeyPath);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal(3, diagnostic.Column);
    }

    // ------------------------------------------- the runner end to end ----

    [Fact]
    public async Task Runner_RejectsABrokenProjectFileWithConfigError()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "themee": "nord" } }""");
        var (_, path) = WriteRunConfig(workspace);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", path], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        var text = stderr.ToString();
        Assert.Contains(ConfigDiagnosticCodes.UnknownKey, text, StringComparison.Ordinal);
        Assert.Contains("andy.jsonc:1:11", text, StringComparison.Ordinal);
        Assert.Contains("[ui.themee]", text, StringComparison.Ordinal);
        Assert.Contains("--isolated", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_IsolatedIgnoresTheBrokenProjectFile()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "themee": "nord" } }""");
        var (_, path) = WriteRunConfig(workspace);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", path, "--isolated"], stdout, stderr);

        // The run proceeds past configuration; it fails later for want of a real
        // provider, which is a different exit code from ConfigError.
        Assert.NotEqual(HeadlessExitCode.ConfigError, code);
    }

    [Fact]
    public async Task Runner_NoProjectConfigIsASynonymForIsolated()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "themee": "nord" } }""");
        var (_, path) = WriteRunConfig(workspace);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", path, "--no-project-config"], stdout, stderr);

        Assert.NotEqual(HeadlessExitCode.ConfigError, code);
    }

    [Fact]
    public async Task Runner_UsageMentionsTheIsolationFlag()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await HeadlessRunner.RunAsync(["run", "--headless"], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("--isolated", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_UnknownArgumentIsStillRejected()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", "x", "--auto"], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("Unknown argument: --auto", stderr.ToString(), StringComparison.Ordinal);
    }
}
