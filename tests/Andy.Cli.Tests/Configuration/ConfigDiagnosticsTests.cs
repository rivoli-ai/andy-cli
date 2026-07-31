using System;
using System.IO;
using System.Linq;
using Andy.Cli.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// Every failure mode must name its source precisely. A configuration error the
/// user cannot locate is barely better than no error at all, so each test here
/// asserts the file, the line, the column and the dotted key path - not just that
/// something went wrong.
/// </summary>
public class ConfigDiagnosticsTests
{
    // ------------------------------------------------------ invalid JSONC ----

    [Fact]
    public void InvalidJsonc_ReportsFileLineAndColumn()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("{\n  \"ui\": {\n    \"theme\": \"nord\",\n  \n");

        var effective = workspace.Load();
        var diagnostic = Assert.Single(
            effective.Errors, d => d.Code == ConfigDiagnosticCodes.InvalidJson);

        Assert.Equal(workspace.ProjectConfigPath, diagnostic.Source.FilePath);
        Assert.Equal(ConfigSourceKind.Project, diagnostic.Source.Kind);
        Assert.True(diagnostic.Line > 0, "a parse error must carry a line number");
        Assert.True(diagnostic.Column > 0, "a parse error must carry a column");
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAccepted()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              // Line comment: the file is JSONC, not JSON.
              /* Block comment. */
              "ui": {
                "theme": "nord",
              },
            }
            """);

        var effective = workspace.Load();

        Assert.False(effective.HasErrors, string.Join("; ", effective.Errors));
        Assert.Equal("nord", effective.Config.Ui.Theme);
    }

    [Fact]
    public void NonObjectRoot_IsRejectedWithAPosition()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("[1, 2, 3]");

        var effective = workspace.Load();
        var diagnostic = Assert.Single(
            effective.Errors, d => d.Code == ConfigDiagnosticCodes.InvalidJson);

        Assert.Equal(workspace.UserConfigPath, diagnostic.Source.FilePath);
        Assert.Contains("object", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------- unknown keys ----

    [Fact]
    public void UnknownTopLevelKey_ReportsSourceLineColumnAndKeyPath()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "ui": { "theme": "nord" },
              "formatter": { "onSave": true }
            }
            """);

        var effective = workspace.Load();
        var diagnostic = Assert.Single(
            effective.Errors, d => d.Code == ConfigDiagnosticCodes.UnknownKey);

        Assert.Equal(workspace.ProjectConfigPath, diagnostic.Source.FilePath);
        Assert.Equal("formatter", diagnostic.KeyPath);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal(3, diagnostic.Column);
        Assert.Contains("unknown key 'formatter'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownNestedKey_ReportsTheFullKeyPath()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""
            {
              "llm": {
                "providers": {
                  "openai": {
                    "apiKeyy": "x"
                  }
                }
              }
            }
            """);

        var effective = workspace.Load();
        var diagnostic = Assert.Single(
            effective.Errors, d => d.Code == ConfigDiagnosticCodes.UnknownKey);

        Assert.Equal("llm.providers.openai.apiKeyy", diagnostic.KeyPath);
        Assert.Equal(ConfigSourceKind.User, diagnostic.Source.Kind);
        Assert.Equal(5, diagnostic.Line);
        Assert.Contains("Did you mean 'apiKey'?", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongCaseKey_IsAnUnknownKeyRatherThanSilentlyAccepted()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "UI": { "theme": "nord" } }""");

        var effective = workspace.Load();

        Assert.Contains(
            effective.Errors,
            d => d.Code == ConfigDiagnosticCodes.UnknownKey && d.KeyPath == "UI");
    }

    [Fact]
    public void UnknownKeysInDifferentFiles_EachIdentifyTheirOwnFile()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "nopeUser": 1 }""");
        workspace.WriteProject("""{ "nopeProject": 1 }""");

        var errors = workspace.Load()
            .Errors.Where(d => d.Code == ConfigDiagnosticCodes.UnknownKey)
            .ToList();

        Assert.Equal(2, errors.Count);
        Assert.Equal(
            workspace.UserConfigPath,
            errors.Single(e => e.KeyPath == "nopeUser").Source.FilePath);
        Assert.Equal(
            workspace.ProjectConfigPath,
            errors.Single(e => e.KeyPath == "nopeProject").Source.FilePath);
    }

    // ------------------------------------------------------ invalid values ----

    [Fact]
    public void WrongType_IsReportedAtTheValuePosition()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "ui": {
                "transparentBackground": "yes"
              }
            }
            """);

        var effective = workspace.Load();
        var diagnostic = Assert.Single(
            effective.Errors, d => d.Code == ConfigDiagnosticCodes.InvalidValue);

        Assert.Equal("ui.transparentBackground", diagnostic.KeyPath);
        Assert.Equal(workspace.ProjectConfigPath, diagnostic.Source.FilePath);
        Assert.Equal(3, diagnostic.Line);
    }

    [Fact]
    public void ValueOutsideTheEnum_IsRejected()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "logging": { "level": "shouty" } }""");

        var effective = workspace.Load();

        Assert.Contains(
            effective.Errors,
            d => d.Code == ConfigDiagnosticCodes.InvalidValue && d.KeyPath == "logging.level");
    }

    [Fact]
    public void NumberBelowTheMinimum_IsRejected()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "session": { "maxTurns": 0 } }""");

        var effective = workspace.Load();

        Assert.Contains(
            effective.Errors,
            d => d.Code == ConfigDiagnosticCodes.InvalidValue && d.KeyPath == "session.maxTurns");
    }

    [Fact]
    public void WrongSchemaVersion_IsRejected()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "version": 99 }""");

        var effective = workspace.Load();

        Assert.Contains(
            effective.Errors,
            d => d.Code == ConfigDiagnosticCodes.InvalidValue && d.KeyPath == "version");
    }

    // ------------------------------------------------ missing substitution ----

    [Fact]
    public void MissingEnvironmentVariable_IsAnErrorNamingTheVariableAndPosition()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "llm": {
                "providers": {
                  "openai": { "apiKey": "{env:DEFINITELY_NOT_SET_280}" }
                }
              }
            }
            """);

        var effective = workspace.Load();
        var diagnostic = Assert.Single(
            effective.Errors, d => d.Code == ConfigDiagnosticCodes.MissingSubstitution);

        Assert.Equal("llm.providers.openai.apiKey", diagnostic.KeyPath);
        Assert.Equal(workspace.ProjectConfigPath, diagnostic.Source.FilePath);
        Assert.Equal(4, diagnostic.Line);
        Assert.Contains("DEFINITELY_NOT_SET_280", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingVariableInsideAnArray_ReportsTheElementIndex()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "mcp": {
                "servers": {
                  "files": {
                    "transport": "stdio",
                    "command": "npx",
                    "args": ["-y", "{env:MISSING_ARG_280}"]
                  }
                }
              }
            }
            """);

        var effective = workspace.Load();
        var diagnostic = Assert.Single(
            effective.Errors, d => d.Code == ConfigDiagnosticCodes.MissingSubstitution);

        Assert.Equal("mcp.servers.files.args[1]", diagnostic.KeyPath);
        Assert.Equal(7, diagnostic.Line);
    }

    [Fact]
    public void LegacyDollarBraceSyntax_IsAlsoResolved()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithEnvironment("LEGACY_TOKEN_280", "legacy-value");
        workspace.WriteProject("""
            { "llm": { "providers": { "openai": { "apiBase": "https://x/${LEGACY_TOKEN_280}" } } } }
            """);

        var effective = workspace.Load();

        Assert.False(effective.HasErrors, string.Join("; ", effective.Errors));
        Assert.Equal("https://x/legacy-value", effective.Config.Llm.Providers["openai"].ApiBase);
    }

    [Fact]
    public void MissingVariableInThePackagedDefaults_IsOnlyAWarning()
    {
        // The defaults layer declares {env:...} for every provider, including the
        // ones this machine has no credentials for. That must not fail a load.
        using var workspace = new ConfigTestWorkspace();

        var effective = workspace.Load();

        Assert.False(effective.HasErrors, string.Join("; ", effective.Errors));
        Assert.Contains(
            effective.Warnings,
            d => d.Code == ConfigDiagnosticCodes.MissingSubstitution
                && d.Source.Kind == ConfigSourceKind.PackagedDefaults);
    }

    // ---------------------------------------------------------- bad paths ----

    [Fact]
    public void RelativePath_IsResolvedAgainstTheDeclaringFile()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProjectDotAndy("""{ "session": { "directory": "state/sessions" } }""");

        var effective = workspace.Load();

        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.WorkspaceDirectory, ".andy", "state", "sessions")),
            effective.Config.Session.Directory);
    }

    [Fact]
    public void RelativePathsInDifferentFiles_ResolveAgainstDifferentDirectories()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "session": { "directory": "user-sessions" } }""");

        var fromUser = workspace.Load();
        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.HomeDirectory, ".andy", "user-sessions")),
            fromUser.Config.Session.Directory);

        workspace.WriteProject("""{ "session": { "directory": "project-sessions" } }""");

        var fromProject = workspace.Load();
        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.WorkspaceDirectory, "project-sessions")),
            fromProject.Config.Session.Directory);
    }

    [Fact]
    public void McpWorkingDirectory_IsResolvedAgainstTheDeclaringFile()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "mcp": {
                "servers": {
                  "files": { "transport": "stdio", "command": "npx", "workingDirectory": "./tools" }
                }
              }
            }
            """);

        var effective = workspace.Load();

        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.WorkspaceDirectory, "tools")),
            effective.Config.Mcp.Servers["files"].WorkingDirectory);
    }

    [Fact]
    public void UnusablePath_IsReportedAgainstTheFileThatDeclaredIt()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("{\n  \"session\": {\n    \"directory\": \"bad\\u0000path\"\n  }\n}");

        var effective = workspace.Load();
        var diagnostic = Assert.Single(
            effective.Errors, d => d.Code == ConfigDiagnosticCodes.InvalidPath);

        Assert.Equal("session.directory", diagnostic.KeyPath);
        Assert.Equal(workspace.ProjectConfigPath, diagnostic.Source.FilePath);
        Assert.Equal(3, diagnostic.Line);
    }

    [Fact]
    public void TildePath_ExpandsToTheHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.True(ConfigPathResolver.TryResolve("~/andy-sessions", "/base", out var resolved, out _));

        Assert.Equal(Path.GetFullPath(Path.Combine(home, "andy-sessions")), resolved);
    }
}
