using Andy.Cli.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

public class ProviderExtraBodyTests
{
    private static IConfiguration Config(Dictionary<string, string?> kv)
        => new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public void Resolve_RebuildsTypedProviderRouting_FromFlattenedConfig()
    {
        // IConfiguration flattens JSON to these keys; arrays become index-keyed children.
        var cfg = Config(new()
        {
            ["Llm:Providers:openrouter:ExtraBody:provider:order:0"] = "deepinfra/turbo",
            ["Llm:Providers:openrouter:ExtraBody:provider:allow_fallbacks"] = "false",
            ["Llm:Providers:openrouter:ExtraBody:models:0"] = "deepseek/deepseek-r1",
            ["Llm:Providers:openrouter:ExtraBody:models:1"] = "openai/gpt-5",
        });

        var extra = ProviderExtraBody.Resolve(cfg, "openrouter");

        Assert.NotNull(extra);
        var provider = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(extra!["provider"]);
        // allow_fallbacks is a real bool, not the string "false".
        Assert.Equal(false, provider["allow_fallbacks"]);
        var order = Assert.IsAssignableFrom<List<object?>>(provider["order"]);
        Assert.Equal("deepinfra/turbo", order[0]);
        // models is a string array.
        var models = Assert.IsAssignableFrom<List<object?>>(extra["models"]);
        Assert.Equal(new object?[] { "deepseek/deepseek-r1", "openai/gpt-5" }, models);
    }

    [Fact]
    public void Resolve_InfersNumericAndBooleanScalars()
    {
        var cfg = Config(new()
        {
            ["Llm:Providers:openrouter:ExtraBody:provider:max_price:prompt"] = "1",
            ["Llm:Providers:openrouter:ExtraBody:provider:require_parameters"] = "true",
            ["Llm:Providers:openrouter:ExtraBody:temperature"] = "0.2",
        });

        var extra = ProviderExtraBody.Resolve(cfg, "openrouter")!;
        var provider = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(extra["provider"]);
        var maxPrice = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(provider["max_price"]);
        Assert.Equal(1L, maxPrice["prompt"]);
        Assert.Equal(true, provider["require_parameters"]);
        Assert.Equal(0.2d, extra["temperature"]);
    }

    [Fact]
    public void Resolve_NoExtraBody_ReturnsNull()
    {
        var cfg = Config(new() { ["Llm:Providers:openrouter:Model"] = "anthropic/claude-sonnet-4.6" });
        Assert.Null(ProviderExtraBody.Resolve(cfg, "openrouter"));
    }
}
