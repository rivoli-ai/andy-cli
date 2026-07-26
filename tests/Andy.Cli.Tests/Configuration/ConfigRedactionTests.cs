using System;
using System.Collections.Generic;
using Andy.Cli.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// <c>config show</c> is the one place the whole configuration becomes text, so it
/// is the one place a credential can escape. These tests assert on the RENDERED
/// output, not on a helper, because that is what a user pastes into an issue.
/// </summary>
public class ConfigRedactionTests
{
    private const string Secret = "sk-super-secret-value-280";

    [Fact]
    public void ApiKeyFromAFile_IsNeverPrinted()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject($$"""
            { "llm": { "providers": { "openai": { "apiKey": "{{Secret}}" } } } }
            """);

        var output = ConfigReportFormatter.FormatEffective(workspace.Load(), includeSources: true);

        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
        Assert.Contains("llm.providers.openai.apiKey", output, StringComparison.Ordinal);
        Assert.Contains(ConfigRedactor.Placeholder, output, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiKeyFromTheEnvironment_IsNeverPrinted()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithEnvironment("OPENAI_API_KEY", Secret);

        var output = ConfigReportFormatter.FormatEffective(workspace.Load(), includeSources: true);

        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstitutedSecret_IsRedactedEvenInsideAnOtherwiseHarmlessField()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithEnvironment("EMBEDDED_TOKEN_280", Secret);
        workspace.WriteProject("""
            {
              "llm": {
                "providers": {
                  "openai": { "apiBase": "https://gateway.example.test/{env:EMBEDDED_TOKEN_280}/v1" }
                }
              }
            }
            """);

        var effective = workspace.Load();
        var output = ConfigReportFormatter.FormatEffective(effective, includeSources: true);

        // The value really was substituted...
        Assert.Contains(Secret, effective.Config.Llm.Providers["openai"].ApiBase!, StringComparison.Ordinal);
        // ...but printing it is not allowed.
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderValues_AreAlwaysRedacted()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""
            {
              "mcp": {
                "servers": {
                  "api": {
                    "transport": "http",
                    "url": "https://mcp.example.test/rpc",
                    "headers": { "Authorization": "Bearer plain-text-bearer-280", "X-Team": "core" }
                  }
                }
              }
            }
            """);

        var output = ConfigReportFormatter.FormatEffective(workspace.Load(), includeSources: false);

        Assert.DoesNotContain("plain-text-bearer-280", output, StringComparison.Ordinal);
        // Even a header that looks innocuous is redacted: the loader cannot know
        // which of a server's headers carries the credential.
        Assert.DoesNotContain("= core", output, StringComparison.Ordinal);
        Assert.Contains("mcp.servers.api.headers.X-Team", output, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOutput_IsRedactedToo()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithEnvironment("ANTHROPIC_API_KEY", Secret);

        var output = ConfigReportFormatter.FormatJson(workspace.Load(), includeSources: true);

        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
        Assert.Contains("llm.providers.anthropic.apiKey", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticMessages_AreScrubbedOfResolvedSecrets()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal) { Secret };

        var scrubbed = ConfigRedactor.Scrub($"failed while using {Secret} as a key", secrets);

        Assert.DoesNotContain(Secret, scrubbed, StringComparison.Ordinal);
        Assert.Contains(ConfigRedactor.Placeholder, scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("llm.providers.openai.apiKey")]
    [InlineData("mcp.servers.api.headers.X-Anything")]
    [InlineData("some.section.accessToken")]
    [InlineData("some.section.password")]
    [InlineData("some.section.clientSecret")]
    [InlineData("some.section.authorization")]
    public void SensitivePaths_AreRecognised(string keyPath)
    {
        Assert.True(ConfigRedactor.IsSensitivePath(keyPath));
    }

    [Theory]
    [InlineData("ui.theme")]
    [InlineData("mcp.servers.api.url")]
    [InlineData("llm.providers.openai.model")]
    [InlineData("session.directory")]
    public void OrdinaryPaths_ArePrintedAsIs(string keyPath)
    {
        Assert.False(ConfigRedactor.IsSensitivePath(keyPath));
    }

    [Fact]
    public void NonSecretValues_AreStillVisible()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "theme": "gruvbox" } }""");

        var output = ConfigReportFormatter.FormatEffective(workspace.Load(), includeSources: true);

        Assert.Contains("gruvbox", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortSubstitutedValues_DoNotTurnTheWholeReportIntoPlaceholders()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WithEnvironment("TINY_280", "eu");
        workspace.WriteProject("""{ "ui": { "theme": "{env:TINY_280}" } }""");

        var output = ConfigReportFormatter.FormatEffective(workspace.Load(), includeSources: false);

        Assert.Contains("ui.theme", output, StringComparison.Ordinal);
        Assert.Contains("= eu", output, StringComparison.Ordinal);
    }
}
