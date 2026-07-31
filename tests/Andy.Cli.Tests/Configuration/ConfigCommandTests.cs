using System;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// The user-facing surface: <c>andy-cli config validate</c> must fail loudly and
/// locatably, and <c>andy-cli config show --effective --sources</c> must be safe to
/// paste into a bug report.
/// </summary>
public class ConfigCommandTests
{
    private static ConfigCommand CommandFor(ConfigTestWorkspace workspace) =>
        new(workspace.WorkspaceDirectory, request => new AndyConfigurationService().Load(
            new ConfigLoadRequest
            {
                WorkspaceDirectory = workspace.WorkspaceDirectory,
                UserHomeDirectory = workspace.HomeDirectory,
                AppSettingsPath = string.Empty,
                CommandLineArguments = request.CommandLineArguments,
                EnvironmentOverride = workspace.Environment,
            }));

    [Fact]
    public async Task Validate_SucceedsOnACleanTree()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "theme": "nord" } }""");

        var result = await CommandFor(workspace).ExecuteAsync(new[] { "validate" });

        Assert.True(result.Success, result.Message);
        Assert.Contains("Configuration is valid.", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_FailsAndPointsAtTheOffendingKey()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "ui": { "theme": "nord" },
              "lsp": { "servers": {} }
            }
            """);

        var result = await CommandFor(workspace).ExecuteAsync(new[] { "validate" });

        Assert.False(result.Success);
        Assert.Contains(ConfigDiagnosticCodes.UnknownKey, result.Message, StringComparison.Ordinal);
        Assert.Contains("andy.jsonc:3:3", result.Message, StringComparison.Ordinal);
        Assert.Contains("[lsp]", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_ReportsEveryLayerNotJustTheFirstFailure()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "nopeUser": 1 }""");
        workspace.WriteProject("""{ "nopeProject": 1 }""");

        var result = await CommandFor(workspace).ExecuteAsync(new[] { "validate" });

        Assert.False(result.Success);
        Assert.Contains("nopeUser", result.Message, StringComparison.Ordinal);
        Assert.Contains("nopeProject", result.Message, StringComparison.Ordinal);
        Assert.Contains("2 errors", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_WithEffectiveAndSources_AnnotatesEveryValue()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "theme": "gruvbox" } }""");

        var result = await CommandFor(workspace)
            .ExecuteAsync(new[] { "show", "--effective", "--sources" });

        Assert.True(result.Success, result.Message);
        Assert.Contains("ui.theme", result.Message, StringComparison.Ordinal);
        Assert.Contains("gruvbox", result.Message, StringComparison.Ordinal);
        Assert.Contains("project:", result.Message, StringComparison.Ordinal);
        Assert.Contains(
            "packaged defaults < user < project < environment < CLI arguments",
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_RedactsSecrets()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithEnvironment("GROQ_API_KEY", "gsk-do-not-print-280");

        var result = await CommandFor(workspace)
            .ExecuteAsync(new[] { "show", "--effective", "--sources" });

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("gsk-do-not-print-280", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_RevealsThePermissionRuleFileLocations()
    {
        // The rules keep their own security format, but their whereabouts must be
        // discoverable from the effective configuration.
        using var workspace = new ConfigTestWorkspace();

        var result = await CommandFor(workspace).ExecuteAsync(new[] { "show" });

        Assert.Contains("permissions.user", result.Message, StringComparison.Ordinal);
        Assert.Contains("permissions.project", result.Message, StringComparison.Ordinal);
        Assert.Contains("permissions.local", result.Message, StringComparison.Ordinal);
        Assert.Contains("not merged into this configuration", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_Json_IsParseable()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "theme": "nord" } }""");

        var result = await CommandFor(workspace).ExecuteAsync(new[] { "show", "--json", "--sources" });

        var document = System.Text.Json.Nodes.JsonNode.Parse(result.Message);
        Assert.NotNull(document);
        Assert.Equal(ConfigSchema.Version, document!["schemaVersion"]!.GetValue<int>());
        Assert.Equal("nord", document["values"]!["ui.theme"]!["value"]!.GetValue<string>());
        Assert.Equal("project", document["values"]!["ui.theme"]!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sources_ListsLayersLowestPrecedenceFirst()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("{}");

        var result = await CommandFor(workspace).ExecuteAsync(new[] { "sources" });

        var packagedIndex = result.Message.IndexOf("packaged defaults", StringComparison.Ordinal);
        var userIndex = result.Message.IndexOf("user", StringComparison.Ordinal);
        var cliIndex = result.Message.IndexOf("cli", StringComparison.Ordinal);
        Assert.True(packagedIndex >= 0 && packagedIndex < userIndex);
        Assert.True(userIndex < cliIndex);
    }

    [Fact]
    public async Task Schema_PrintsTheVersionedSchema()
    {
        using var workspace = new ConfigTestWorkspace();

        var result = await CommandFor(workspace).ExecuteAsync(new[] { "schema" });

        Assert.True(result.Success);
        Assert.Contains("andy-config.v1.json", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownSubcommand_Fails()
    {
        using var workspace = new ConfigTestWorkspace();

        var result = await CommandFor(workspace).ExecuteAsync(new[] { "frobnicate" });

        Assert.False(result.Success);
        Assert.Contains("Unknown config subcommand", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_DocumentsThePrecedenceAndTheFileLocations()
    {
        using var workspace = new ConfigTestWorkspace();

        var result = await CommandFor(workspace).ExecuteAsync(new[] { "--help" });

        Assert.True(result.Success);
        Assert.Contains("~/.andy/andy.jsonc", result.Message, StringComparison.Ordinal);
        Assert.Contains("<workspace>/.andy/andy.jsonc", result.Message, StringComparison.Ordinal);
    }
}
