using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Andy.Cli.Lsp;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Lsp;

/// <summary>
/// The minimal, self-contained configuration source for language servers.
///
/// This is the seam that issue #280 (layered user/project configuration) will replace; the tests
/// pin the CONTRACT (what a definition means, what is rejected, what nothing-configured does)
/// rather than the file format, so they survive that swap.
/// </summary>
public sealed class LspConfigurationLoaderTests
{
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "andy-lspcfg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void NothingConfiguredYieldsNoServersAndNoErrors()
    {
        var directory = NewDirectory();
        try
        {
            var result = LspConfigurationLoader.Load(null, directory);

            Assert.Empty(result.Servers);
            Assert.Empty(result.Errors);
            Assert.False(result.AllowOutsideWorkspace);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ProjectFileDefinesServersByCommandExtensionsAndRootMarkers()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".andy"));
            File.WriteAllText(Path.Combine(directory, ".andy", LspConfigurationLoader.ProjectFileName), """
            {
              "servers": {
                "typescript": {
                  "command": "typescript-language-server",
                  "args": ["--stdio"],
                  "env": { "NODE_OPTIONS": "--max-old-space-size=4096" },
                  "extensions": [".ts", ".tsx"],
                  "rootMarkers": ["tsconfig.json", "package.json"],
                  "languageId": "typescript",
                  "diagnosticsTimeoutMs": 5000
                }
              }
            }
            """);

            var result = LspConfigurationLoader.Load(null, directory);

            var server = Assert.Single(result.Servers);
            Assert.Equal("typescript", server.Id);
            Assert.Equal("typescript-language-server", server.Command);
            Assert.Equal(new[] { "--stdio" }, server.Args);
            Assert.Equal("--max-old-space-size=4096", server.Environment["NODE_OPTIONS"]);
            Assert.Equal(new[] { ".ts", ".tsx" }, server.Extensions);
            Assert.Equal(new[] { "tsconfig.json", "package.json" }, server.RootMarkers);
            Assert.Equal("typescript", server.EffectiveLanguageId);
            Assert.Equal(5000, server.DiagnosticsTimeoutMs);
            Assert.True(server.Enabled);
            Assert.Empty(result.Errors);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AllowOutsideWorkspaceMustBeOptedIntoExplicitly()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".andy"));
            File.WriteAllText(Path.Combine(directory, ".andy", LspConfigurationLoader.ProjectFileName), """
            { "allowOutsideWorkspace": true, "servers": { "x": { "command": "x", "extensions": [".x"] } } }
            """);

            Assert.True(LspConfigurationLoader.Load(null, directory).AllowOutsideWorkspace);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InvalidDefinitionsAreRejectedWithAnExplanation()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".andy"));
            File.WriteAllText(Path.Combine(directory, ".andy", LspConfigurationLoader.ProjectFileName), """
            {
              "servers": {
                "nocommand": { "extensions": [".a"] },
                "noextensions": { "command": "thing" },
                "good": { "command": "thing", "extensions": [".b"] }
              }
            }
            """);

            var result = LspConfigurationLoader.Load(null, directory);

            Assert.Equal(new[] { "good" }, result.Servers.Select(s => s.Id));
            Assert.Contains(result.Errors, e => e.Contains("nocommand") && e.Contains("command"));
            Assert.Contains(result.Errors, e => e.Contains("noextensions") && e.Contains("extensions"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MalformedJsonIsReportedRatherThanThrown()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".andy"));
            File.WriteAllText(Path.Combine(directory, ".andy", LspConfigurationLoader.ProjectFileName), "{ not json");

            var result = LspConfigurationLoader.Load(null, directory);

            Assert.Empty(result.Servers);
            Assert.Contains(result.Errors, e => e.Contains("invalid JSON"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ProjectFileOverridesAppSettings()
    {
        var directory = NewDirectory();
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Lsp:Servers:csharp:Command"] = "from-appsettings",
                    ["Lsp:Servers:csharp:Extensions:0"] = ".cs",
                })
                .Build();

            Directory.CreateDirectory(Path.Combine(directory, ".andy"));
            File.WriteAllText(Path.Combine(directory, ".andy", LspConfigurationLoader.ProjectFileName), """
            { "servers": { "csharp": { "command": "from-project" } } }
            """);

            var result = LspConfigurationLoader.Load(configuration, directory);

            var server = Assert.Single(result.Servers);
            Assert.Equal("from-project", server.Command);
            // Fields the project file did not restate are inherited, not lost.
            Assert.Equal(new[] { ".cs" }, server.Extensions);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DefinitionsClaimFilesByExtensionOnly()
    {
        var definition = new LspServerDefinition
        {
            Id = "csharp",
            Command = "x",
            Extensions = new[] { ".cs", "csx" },
        };

        Assert.True(definition.Matches("/w/Program.cs"));
        Assert.True(definition.Matches("/w/Script.CSX"));
        Assert.False(definition.Matches("/w/readme.md"));
        Assert.False(definition.Matches("/w/Makefile"));
    }
}
