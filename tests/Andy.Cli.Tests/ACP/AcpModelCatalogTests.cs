using System;
using Andy.Cli.ACP;
using Andy.Llm.Configuration;
using Andy.Model.Llm;
using Xunit;

namespace Andy.Cli.Tests.ACP;

public class AcpModelCatalogTests
{
    [Fact]
    public void Build_UsesConfiguredModelsAndFiltersUnavailableProviders()
    {
        var options = new LlmOptions
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openai"] = new()
                {
                    Provider = "openai",
                    Model = "gpt-4.1"
                }
            }
        };

        var selections = AcpModelCatalog.Build(
            options,
            "openrouter",
            "moonshotai/kimi-k3",
            provider => provider == "openai");

        Assert.Collection(
            selections,
            selection =>
            {
                Assert.Equal("openrouter", selection.ProviderId);
                Assert.Equal("moonshotai/kimi-k3", selection.ModelId);
            },
            selection =>
            {
                Assert.Equal("openai", selection.ProviderId);
                Assert.Equal("gpt-4.1", selection.ModelId);
                Assert.Equal("openai::gpt-4.1", selection.ValueId);
            });
    }

    [Fact]
    public void Build_RetainsUnknownActiveSelection()
    {
        var selections = AcpModelCatalog.Build(
            options: null,
            "custom",
            "custom-model",
            _ => false);

        var selection = Assert.Single(selections);
        Assert.Equal("custom", selection.ProviderId);
        Assert.Equal("custom-model", selection.ModelId);
        Assert.Equal("custom::custom-model", selection.ValueId);
    }
}
