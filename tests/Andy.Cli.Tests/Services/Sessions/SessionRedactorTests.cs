using System;
using System.Text.Json;
using Andy.Cli.Services.Sessions;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Redaction applied to persisted session transcripts (issue #231), mirroring the
/// headless transcript conventions: sensitive property names, bearer tokens,
/// key/value secrets, provider API key shapes, and known secret values.
/// </summary>
public class SessionRedactorTests
{
    [Fact]
    public void RedactText_RemovesBearerTokens()
    {
        var redactor = new SessionRedactor(Array.Empty<string>());
        var result = redactor.RedactText("Authorization used Bearer abc123.def-456 today");
        Assert.DoesNotContain("abc123.def-456", result);
        Assert.Contains("Bearer [REDACTED]", result);
    }

    [Fact]
    public void RedactText_RemovesKeyValueSecrets()
    {
        var redactor = new SessionRedactor(Array.Empty<string>());
        var result = redactor.RedactText("set api_key=abcd1234 and password: hunter2");
        Assert.DoesNotContain("abcd1234", result);
        Assert.DoesNotContain("hunter2", result);
    }

    [Fact]
    public void RedactText_RemovesProviderApiKeyShapes()
    {
        var redactor = new SessionRedactor(Array.Empty<string>());
        Assert.DoesNotContain("sk-proj4bcdefgh1jkl", redactor.RedactText("my key sk-proj4bcdefgh1jkl leaked"));
        Assert.DoesNotContain("sk-or-abcdefgh1234", redactor.RedactText("router key sk-or-abcdefgh1234"));
    }

    [Fact]
    public void RedactText_RemovesKnownSecretValues()
    {
        var redactor = new SessionRedactor(new[] { "my-literal-secret" });
        var result = redactor.RedactText("the value my-literal-secret appeared in output");
        Assert.DoesNotContain("my-literal-secret", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void RedactJson_ReplacesSensitivePropertiesAndKeepsStructure()
    {
        var redactor = new SessionRedactor(Array.Empty<string>());
        var json = "{\"name\":\"safe\",\"api_key\":\"topsecret\",\"nested\":{\"authorization\":\"Basic xyz\",\"note\":\"plain\"}}";

        var redacted = redactor.RedactJson(json);

        using var document = JsonDocument.Parse(redacted); // still valid JSON
        var root = document.RootElement;
        Assert.Equal("safe", root.GetProperty("name").GetString());
        Assert.Equal("[REDACTED]", root.GetProperty("api_key").GetString());
        Assert.Equal("[REDACTED]", root.GetProperty("nested").GetProperty("authorization").GetString());
        Assert.Equal("plain", root.GetProperty("nested").GetProperty("note").GetString());
    }

    [Fact]
    public void RedactJson_ScrubsStringValuesInsideArrays()
    {
        var redactor = new SessionRedactor(Array.Empty<string>());
        var redacted = redactor.RedactJson("[\"Bearer tok-en.123\",42,null]");

        using var document = JsonDocument.Parse(redacted);
        Assert.Equal("Bearer [REDACTED]", document.RootElement[0].GetString());
        Assert.Equal(42, document.RootElement[1].GetInt32());
    }

    [Fact]
    public void RedactJson_FallsBackToTextRedactionForNonJson()
    {
        var redactor = new SessionRedactor(Array.Empty<string>());
        var result = redactor.RedactJson("not json, but has api_key=oops123");
        Assert.DoesNotContain("oops123", result);
    }
}
