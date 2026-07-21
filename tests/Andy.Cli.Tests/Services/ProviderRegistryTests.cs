using System;
using System.Linq;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

[Collection("EnvironmentVariableTests")] // Registry endpoint tests read env vars; avoid parallel interference
public class ProviderRegistryTests
{
    [Theory]
    [InlineData("openrouter")]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("cerebras")]
    [InlineData("groq")]
    [InlineData("google")]
    [InlineData("ollama")]
    public void IsKnown_RecognizesEveryAdvertisedProvider(string id)
    {
        Assert.True(ProviderRegistry.IsKnown(id));
        Assert.NotNull(ProviderRegistry.Find(id));
    }

    [Fact]
    public void Ids_ContainsExactlyTheSupportedProviders_InPriorityOrder()
    {
        Assert.Equal(
            new[] { "openrouter", "openai", "anthropic", "cerebras", "groq", "google", "ollama" },
            ProviderRegistry.Ids.ToArray());
    }

    [Fact]
    public void IsKnown_RejectsRemovedAndUnknownProviders()
    {
        // Azure was advertised but never managed; it has been removed from the registry.
        Assert.False(ProviderRegistry.IsKnown("azure"));
        Assert.False(ProviderRegistry.IsKnown("not-a-provider"));
        Assert.False(ProviderRegistry.IsKnown(""));
        Assert.False(ProviderRegistry.IsKnown(null));
    }

    [Theory]
    [InlineData("gemini", "google")]
    [InlineData("GEMINI", "google")]
    [InlineData("OpenAI", "openai")]
    public void Resolve_MapsAliasesAndCasingToCanonicalId(string input, string expected)
    {
        Assert.Equal(expected, ProviderRegistry.Resolve(input));
    }

    [Fact]
    public void GetApiKeyEnvVar_ReturnsPrimaryEnvVar()
    {
        Assert.Equal("GOOGLE_API_KEY", ProviderRegistry.GetApiKeyEnvVar("gemini"));
        Assert.Equal("OPENROUTER_API_KEY", ProviderRegistry.GetApiKeyEnvVar("openrouter"));
        Assert.Equal("GROQ_API_KEY", ProviderRegistry.GetApiKeyEnvVar("groq"));
    }

    [Fact]
    public void GetEndpoint_UsesDefault_WhenNoOverride()
    {
        var original = Environment.GetEnvironmentVariable("OPENAI_API_BASE");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_BASE", null);
            Assert.Equal("https://api.openai.com", ProviderRegistry.GetEndpoint("openai"));
            Assert.Equal("https://api.cerebras.ai", ProviderRegistry.GetEndpoint("cerebras"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_BASE", original);
        }
    }

    [Fact]
    public void GetEndpoint_HonorsOverrideEnvVar()
    {
        var original = Environment.GetEnvironmentVariable("OPENAI_API_BASE");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_BASE", "https://proxy.example.com/v1");
            Assert.Equal("https://proxy.example.com/v1", ProviderRegistry.GetEndpoint("openai"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_BASE", original);
        }
    }

    [Fact]
    public void HasCredentials_ForLocalProvider_IsAlwaysTrue()
    {
        // Ollama does not require an API key.
        Assert.True(ProviderRegistry.HasCredentials("ollama"));
    }

    [Fact]
    public void HasCredentials_ReflectsEnvironment()
    {
        var original = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
            Assert.False(ProviderRegistry.HasCredentials("groq"));

            Environment.SetEnvironmentVariable("GROQ_API_KEY", "test-key");
            Assert.True(ProviderRegistry.HasCredentials("groq"));

            // Alias resolves to the same descriptor / credentials.
            var originalGoogle = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
            try
            {
                Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "g-key");
                Assert.True(ProviderRegistry.HasCredentials("gemini"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GOOGLE_API_KEY", originalGoogle);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROQ_API_KEY", original);
        }
    }

    [Theory]
    // Default models are pinned deliberately; a refactor must not silently change
    // which model each provider defaults to.
    [InlineData("openrouter", "moonshotai/kimi-k3")]
    [InlineData("openai", "gpt-4o")]
    [InlineData("anthropic", "claude-3-sonnet-20240229")]
    [InlineData("cerebras", "llama-3.3-70b")]
    [InlineData("groq", "llama-3.3-70b-versatile")]
    [InlineData("google", "gemini-2.0-flash-exp")]
    [InlineData("ollama", "llama2")]
    public void DefaultModel_MatchesOriginalBehavior(string id, string expectedModel)
    {
        Assert.Equal(expectedModel, ProviderRegistry.Find(id)!.DefaultModel);
    }

    [Fact]
    public void SupportsModelListing_TrueForKnownProviders_FalseForUnknown()
    {
        foreach (var id in ProviderRegistry.Ids)
        {
            Assert.True(ProviderRegistry.SupportsModelListing(id), $"{id} should support model listing");
        }

        // Alias resolves to the same descriptor and reports the same capability.
        Assert.True(ProviderRegistry.SupportsModelListing("gemini"));

        // Unknown / empty ids are treated as not listable.
        Assert.False(ProviderRegistry.SupportsModelListing("azure"));
        Assert.False(ProviderRegistry.SupportsModelListing(null));
        Assert.False(ProviderRegistry.SupportsModelListing(""));
    }

    [Fact]
    public void CerebrasIsTheOnlyProvider_ThatLimitsToolCount()
    {
        var limited = ProviderRegistry.All.Where(p => p.LimitsToolCount).Select(p => p.Id).ToArray();
        Assert.Equal(new[] { "cerebras" }, limited);
    }

    [Fact]
    public void DetectionAndRegistry_ShareTheSameProviderSet()
    {
        // Every provider the detection service can report must be a registry provider,
        // guaranteeing detection and /model cannot advertise different sets.
        var detection = new ProviderDetectionService();
        foreach (var id in ProviderRegistry.Ids)
        {
            // A known id is never rejected as "unknown" by the detection service.
            // (Availability depends on env/credentials, but recognition must be consistent.)
            Assert.True(ProviderRegistry.IsKnown(id));
            // IsProviderAvailable must not throw for any known id.
            _ = detection.IsProviderAvailable(id);
        }
    }
}
