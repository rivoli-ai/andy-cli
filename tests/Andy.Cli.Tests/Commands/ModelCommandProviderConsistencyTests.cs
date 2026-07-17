using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Services;
using Andy.Llm.Configuration;
using Andy.Llm.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Cli.Tests.Commands;

/// <summary>
/// Verifies that every provider advertised by the central <see cref="ProviderRegistry"/> is
/// recognized consistently by both credential detection and the /model command. This is the
/// core regression guard for issue #174: a provider detectable at startup (e.g. OpenRouter)
/// must also be switchable via ModelCommand.
/// </summary>
[Collection("EnvironmentVariableTests")] // Serialize env-var mutation across the suite
public class ModelCommandProviderConsistencyTests
{
    // Every credential/endpoint env var the registry or detection can read.
    private static readonly string[] ProviderEnvVars =
    {
        "OPENROUTER_API_KEY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "CEREBRAS_API_KEY",
        "GROQ_API_KEY", "GOOGLE_API_KEY", "OLLAMA_API_BASE", "OPENAI_API_BASE",
        "OPENROUTER_API_BASE", "ANDY_SKIP_OLLAMA", "ANDY_OLLAMA_AUTO_DETECT"
    };

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureLlmFromEnvironment();
        services.Configure<LlmOptions>(options => options.DefaultProvider = "cerebras");
        return services.BuildServiceProvider();
    }

    // Mirrors real startup where no DefaultProvider is pinned, so ModelCommand falls back to
    // credential detection.
    private static IServiceProvider BuildServicesWithAutoDetect()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureLlmFromEnvironment();
        services.Configure<LlmOptions>(options => options.DefaultProvider = "");
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> Snapshot()
    {
        var snapshot = new Dictionary<string, string?>();
        foreach (var name in ProviderEnvVars)
        {
            snapshot[name] = Environment.GetEnvironmentVariable(name);
        }
        return snapshot;
    }

    private static void Restore(Dictionary<string, string?> snapshot)
    {
        foreach (var kvp in snapshot)
        {
            Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
        }
    }

    private static void ClearAll()
    {
        foreach (var name in ProviderEnvVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
        // Never let a locally running Ollama influence these tests.
        Environment.SetEnvironmentVariable("ANDY_SKIP_OLLAMA", "1");
    }

    private static string EnvVarFor(string providerId) => ProviderRegistry.GetApiKeyEnvVar(providerId);

    [Theory]
    [InlineData("openrouter")]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("cerebras")]
    [InlineData("groq")]
    [InlineData("google")]
    public async Task EveryAdvertisedProvider_IsRecognizedByModelCommand(string providerId)
    {
        var snapshot = Snapshot();
        try
        {
            ClearAll();
            Environment.SetEnvironmentVariable(EnvVarFor(providerId), "test-key");

            var command = new ModelCommand(BuildServices());

            // Act
            var result = await command.ExecuteAsync(new[] { "provider", providerId });

            // Assert: recognized and switched (never the "not a recognized provider" error)
            Assert.True(result.Success, $"Provider '{providerId}' was not recognized: {result.Message}");
            Assert.DoesNotContain("not a recognized provider", result.Message ?? "");
            Assert.Equal(providerId, command.GetCurrentProvider());
        }
        finally
        {
            Restore(snapshot);
        }
    }

    [Fact]
    public async Task Ollama_IsRecognizedWithoutApiKey()
    {
        var snapshot = Snapshot();
        try
        {
            ClearAll();
            // Provide an explicit base so detection/switch treat Ollama as available deterministically.
            Environment.SetEnvironmentVariable("OLLAMA_API_BASE", "http://localhost:11434");

            var command = new ModelCommand(BuildServices());
            var result = await command.ExecuteAsync(new[] { "provider", "ollama" });

            Assert.True(result.Success, result.Message);
            Assert.Equal("ollama", command.GetCurrentProvider());
        }
        finally
        {
            Restore(snapshot);
        }
    }

    [Fact]
    public async Task GeminiAlias_ResolvesToGoogleCanonicalId()
    {
        var snapshot = Snapshot();
        try
        {
            ClearAll();
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "test-key");

            var command = new ModelCommand(BuildServices());
            var result = await command.ExecuteAsync(new[] { "provider", "gemini" });

            Assert.True(result.Success, result.Message);
            Assert.Equal("google", command.GetCurrentProvider());
        }
        finally
        {
            Restore(snapshot);
        }
    }

    [Fact]
    public async Task OpenRouter_DetectedAtStartup_IsAlsoSwitchable()
    {
        // Regression for the reported bug: OpenRouter selectable by detection but not by ModelCommand.
        var snapshot = Snapshot();
        try
        {
            ClearAll();
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "test-key");

            var detection = new ProviderDetectionService();
            Assert.Equal("openrouter", detection.DetectDefaultProvider());

            // With no DefaultProvider pinned (as at real startup), the command adopts detection.
            var command = new ModelCommand(BuildServicesWithAutoDetect());
            Assert.Equal("openrouter", command.GetCurrentProvider());

            var result = await command.ExecuteAsync(new[] { "provider", "openrouter" });
            Assert.True(result.Success, result.Message);
            Assert.Equal("openrouter", command.GetCurrentProvider());
        }
        finally
        {
            Restore(snapshot);
        }
    }

    [Fact]
    public async Task MissingCredential_ProducesActionableError()
    {
        var snapshot = Snapshot();
        try
        {
            ClearAll();
            var command = new ModelCommand(BuildServices());

            var result = await command.ExecuteAsync(new[] { "provider", "groq" });

            Assert.False(result.Success);
            Assert.Contains("No API key found", result.Message ?? "");
            Assert.Contains("GROQ_API_KEY", result.Message ?? "");
        }
        finally
        {
            Restore(snapshot);
        }
    }

    [Fact]
    public async Task RemovedProvider_Azure_IsRejected()
    {
        var snapshot = Snapshot();
        try
        {
            ClearAll();
            var command = new ModelCommand(BuildServices());

            var result = await command.ExecuteAsync(new[] { "provider", "azure" });

            Assert.False(result.Success);
            Assert.Contains("not a recognized provider", result.Message ?? "");
        }
        finally
        {
            Restore(snapshot);
        }
    }
}
