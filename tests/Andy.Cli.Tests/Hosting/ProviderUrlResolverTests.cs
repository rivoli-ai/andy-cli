using System;
using Andy.Cli.Hosting;
using Xunit;

namespace Andy.Cli.Tests.Hosting;

/// <summary>
/// Verifies the provider-URL mapping extracted from Program.GetProviderUrl,
/// including the environment-variable overrides and the "unknown" fallback.
/// </summary>
public class ProviderUrlResolverTests
{
    [Theory]
    [InlineData("cerebras", "https://api.cerebras.ai")]
    [InlineData("anthropic", "https://api.anthropic.com")]
    [InlineData("gemini", "https://generativelanguage.googleapis.com")]
    public void Resolve_ReturnsFixedUrls(string provider, string expected)
    {
        Assert.Equal(expected, ProviderUrlResolver.Resolve(provider));
    }

    [Fact]
    public void Resolve_UnknownProvider_ReturnsUnknown()
    {
        Assert.Equal("unknown", ProviderUrlResolver.Resolve("does-not-exist"));
    }

    [Fact]
    public void Resolve_Openai_UsesEnvOverrideWhenSet()
    {
        var original = Environment.GetEnvironmentVariable("OPENAI_API_BASE");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_BASE", "https://proxy.example/v1");
            Assert.Equal("https://proxy.example/v1", ProviderUrlResolver.Resolve("openai"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_BASE", original);
        }
    }

    [Fact]
    public void Resolve_Openai_FallsBackToDefaultWhenEnvUnset()
    {
        var original = Environment.GetEnvironmentVariable("OPENAI_API_BASE");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_BASE", null);
            Assert.Equal("https://api.openai.com", ProviderUrlResolver.Resolve("openai"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_BASE", original);
        }
    }
}
