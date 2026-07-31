using System;
using System.Linq;
using Andy.Cli.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// Merge semantics are per field, and the interesting half of that is what does
/// NOT happen: arrays are not concatenated, a partial override does not wipe its
/// siblings, and a keyed map does not lose entries a lower layer contributed.
/// </summary>
public class ConfigMergeSemanticsTests
{
    [Fact]
    public void Arrays_AreReplaced_NotConcatenated()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""
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
        workspace.WriteProject("""
            { "mcp": { "servers": { "files": { "args": ["--only-this"] } } } }
            """);

        var effective = workspace.Load();

        Assert.Equal(new[] { "--only-this" }, effective.Config.Mcp.Servers["files"].Args);
    }

    [Fact]
    public void EmptyArray_ClearsTheLowerLayer()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""
            { "mcp": { "servers": { "files": { "transport": "stdio", "command": "npx", "args": ["-y"] } } } }
            """);
        workspace.WriteProject("""
            { "mcp": { "servers": { "files": { "args": [] } } } }
            """);

        var effective = workspace.Load();

        Assert.Empty(effective.Config.Mcp.Servers["files"].Args);
    }

    [Fact]
    public void KeyedMaps_MergeEntryByEntry()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""
            {
              "mcp": {
                "servers": {
                  "alpha": { "transport": "stdio", "command": "alpha-cmd" },
                  "beta":  { "transport": "stdio", "command": "beta-cmd" }
                }
              }
            }
            """);
        workspace.WriteProject("""
            {
              "mcp": {
                "servers": {
                  "beta":  { "command": "beta-override" },
                  "gamma": { "transport": "stdio", "command": "gamma-cmd" }
                }
              }
            }
            """);

        var servers = workspace.Load().Config.Mcp.Servers;

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, servers.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("alpha-cmd", servers["alpha"].Command);
        Assert.Equal("beta-override", servers["beta"].Command);
        Assert.Equal("stdio", servers["beta"].Transport);
        Assert.Equal("gamma-cmd", servers["gamma"].Command);
    }

    [Fact]
    public void EnvAndHeaderMaps_MergeKeyByKey()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""
            {
              "mcp": {
                "servers": {
                  "api": {
                    "transport": "http",
                    "url": "https://mcp.example.test/rpc",
                    "headers": { "X-Team": "core", "X-Region": "eu" },
                    "env": { "LOG_LEVEL": "warn", "TZ": "UTC" }
                  }
                }
              }
            }
            """);
        workspace.WriteProject("""
            {
              "mcp": {
                "servers": {
                  "api": {
                    "headers": { "X-Region": "us" },
                    "env": { "LOG_LEVEL": "debug" }
                  }
                }
              }
            }
            """);

        var server = workspace.Load().Config.Mcp.Servers["api"];

        Assert.Equal("core", server.Headers["X-Team"]);
        Assert.Equal("us", server.Headers["X-Region"]);
        Assert.Equal("UTC", server.Env["TZ"]);
        Assert.Equal("debug", server.Env["LOG_LEVEL"]);
    }

    [Fact]
    public void PartialProviderOverride_KeepsTheSiblingFields()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""
            {
              "llm": {
                "providers": {
                  "openai": { "apiBase": "https://user.example.test", "model": "user-model" }
                }
              }
            }
            """);
        workspace.WriteProject("""
            { "llm": { "providers": { "openai": { "model": "project-model" } } } }
            """);

        var provider = workspace.Load().Config.Llm.Providers["openai"];

        Assert.Equal("https://user.example.test", provider.ApiBase);
        Assert.Equal("project-model", provider.Model);
    }

    [Fact]
    public void ExplicitNull_UnsetsTheKeyRatherThanStoringNull()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "ui": { "theme": "nord" } }""");
        workspace.WriteProject("""{ "ui": { "theme": null } }""");

        var effective = workspace.Load();

        Assert.Null(effective.Config.Ui.Theme);
        Assert.Null(effective.OriginOf("ui.theme"));
    }

    [Fact]
    public void ReplacingASubtree_DropsTheProvenanceOfTheValuesItRemoved()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""
            { "mcp": { "servers": { "api": { "transport": "http", "url": "https://a.example.test" } } } }
            """);
        workspace.WriteProject("""{ "mcp": null }""");

        var effective = workspace.Load();

        Assert.Empty(effective.Config.Mcp.Servers);
        Assert.Null(effective.OriginOf("mcp.servers.api.url"));
    }

    [Fact]
    public void ProvenanceIsRecordedPerLeaf_NotPerFile()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteUser("""{ "ui": { "theme": "nord", "transparentBackground": true } }""");
        workspace.WriteProject("""{ "ui": { "theme": "dracula" } }""");

        var effective = workspace.Load();

        Assert.Equal(workspace.ProjectConfigPath, effective.OriginOf("ui.theme")!.Source.FilePath);
        Assert.Equal(
            workspace.UserConfigPath,
            effective.OriginOf("ui.transparentBackground")!.Source.FilePath);
    }

    [Fact]
    public void ProvenanceCarriesTheExactLineAndColumn()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "ui": {
                "theme": "nord"
              }
            }
            """);

        var origin = workspace.Load().OriginOf("ui.theme")!;

        Assert.Equal(3, origin.Line);
        Assert.Equal(14, origin.Column);
        Assert.Contains("andy.jsonc:3:14", origin.ToString(), StringComparison.Ordinal);
    }
}
