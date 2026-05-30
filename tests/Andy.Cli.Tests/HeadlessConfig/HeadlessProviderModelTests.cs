using Andy.Cli.Headless;
using Andy.Llm.Configuration;
using Xunit;

namespace Andy.Cli.Tests.HeadlessConfig;

/// <summary>
/// Tests for HeadlessAgentRunner.ApplyConfiguredModel — the glue that threads
/// the headless config's model id into the provider config the factory builds,
/// so providers that require a model at construction (e.g. OpenRouter) work
/// without an OPENROUTER_MODEL env var.
/// </summary>
public class HeadlessProviderModelTests
{
    [Fact]
    public void ApplyConfiguredModel_ExactKeyExists_SetsModel()
    {
        var options = new LlmOptions
        {
            Providers =
            {
                ["openrouter"] = new ProviderConfig { Provider = "openrouter", ApiKey = "k" }
            }
        };

        HeadlessAgentRunner.ApplyConfiguredModel(options, "openrouter", "xiaomi/mimo-v2.5");

        Assert.Equal("xiaomi/mimo-v2.5", options.Providers["openrouter"].Model);
    }

    [Fact]
    public void ApplyConfiguredModel_OverwritesEnvDerivedModel()
    {
        // An env-derived OPENROUTER_MODEL must not win over the run's explicit
        // config.model.id — the headless config is authoritative for the run.
        var options = new LlmOptions
        {
            Providers =
            {
                ["openrouter"] = new ProviderConfig { Provider = "openrouter", Model = "some/other-model" }
            }
        };

        HeadlessAgentRunner.ApplyConfiguredModel(options, "openrouter", "xiaomi/mimo-v2.5");

        Assert.Equal("xiaomi/mimo-v2.5", options.Providers["openrouter"].Model);
    }

    [Fact]
    public void ApplyConfiguredModel_MatchesByProviderType_WhenKeyDiffers()
    {
        // The factory matches by Provider type when there's no exact key match
        // (e.g. a "cerebras/large" entry for provider "cerebras"); the model
        // must land on that same entry.
        var options = new LlmOptions
        {
            Providers =
            {
                ["openrouter/custom"] = new ProviderConfig { Provider = "openrouter", ApiKey = "k" }
            }
        };

        HeadlessAgentRunner.ApplyConfiguredModel(options, "openrouter", "xiaomi/mimo-v2.5");

        Assert.Equal("xiaomi/mimo-v2.5", options.Providers["openrouter/custom"].Model);
        Assert.False(options.Providers.ContainsKey("openrouter"));
    }

    [Fact]
    public void ApplyConfiguredModel_NoEntry_CreatesOne()
    {
        var options = new LlmOptions();

        HeadlessAgentRunner.ApplyConfiguredModel(options, "openrouter", "xiaomi/mimo-v2.5");

        Assert.True(options.Providers.ContainsKey("openrouter"));
        Assert.Equal("openrouter", options.Providers["openrouter"].Provider);
        Assert.Equal("xiaomi/mimo-v2.5", options.Providers["openrouter"].Model);
    }
}
