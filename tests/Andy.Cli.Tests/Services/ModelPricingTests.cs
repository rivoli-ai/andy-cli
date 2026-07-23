using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class ModelPricingTests
{
    // --- Price lookup -------------------------------------------------------------

    [Theory]
    [InlineData("gpt-4o", 2.50, 10.00)]
    [InlineData("gpt-4o-mini", 0.15, 0.60)]
    [InlineData("gpt-5-codex", 1.25, 10.00)]
    [InlineData("gpt-5.1-codex-mini", 0.25, 2.00)]
    [InlineData("claude-3-5-haiku-20241022", 0.80, 4.00)]
    [InlineData("claude-haiku-4-5", 1.00, 5.00)]
    [InlineData("claude-sonnet-4-6", 3.00, 15.00)]
    [InlineData("claude-opus-4-8", 5.00, 25.00)]
    [InlineData("gemini-2.0-flash-exp", 0.10, 0.40)]
    [InlineData("llama-3.3-70b-versatile", 0.59, 0.79)]
    [InlineData("deepseek-r1", 0.55, 2.19)]
    [InlineData("deepseek-chat", 0.27, 1.10)]
    public void GetPrice_KnownModels_ReturnsExpectedRates(string model, double input, double output)
    {
        var price = ModelPricing.GetPrice(model);
        Assert.NotNull(price);
        Assert.Equal((decimal)input, price!.Value.InputPerMillion);
        Assert.Equal((decimal)output, price.Value.OutputPerMillion);
    }

    [Fact]
    public void GetPrice_StripsProviderPrefix_AndIgnoresCase()
    {
        var price = ModelPricing.GetPrice("OpenAI/GPT-4o");
        Assert.NotNull(price);
        Assert.Equal(2.50m, price!.Value.InputPerMillion);
    }

    [Theory]
    [InlineData("some-unknown-model")]
    [InlineData("")]
    [InlineData(null)]
    public void GetPrice_UnknownModel_ReturnsNull(string? model)
    {
        Assert.Null(ModelPricing.GetPrice(model));
    }

    [Fact]
    public void GetPrice_OllamaProvider_IsFree()
    {
        var price = ModelPricing.GetPrice("llama3.2", "ollama");
        Assert.NotNull(price);
        Assert.Equal(0m, price!.Value.InputPerMillion);
        Assert.Equal(0m, price.Value.OutputPerMillion);
    }

    // --- Cost computation ----------------------------------------------------------

    [Fact]
    public void ComputeCostUsd_UsesInputAndOutputRates()
    {
        // gpt-4o: $2.50 in / $10.00 out per MTok.
        // 100_000 input => $0.25; 10_000 output => $0.10; total $0.35.
        var cost = ModelPricing.ComputeCostUsd("gpt-4o", "openai", 100_000, 10_000);
        Assert.Equal(0.35m, cost);
    }

    [Fact]
    public void ComputeCostUsd_UnknownModel_ReturnsNull()
    {
        Assert.Null(ModelPricing.ComputeCostUsd("mystery-model", "acme", 1000, 1000));
    }

    [Fact]
    public void ComputeCostUsd_ZeroTokens_IsZero()
    {
        Assert.Equal(0m, ModelPricing.ComputeCostUsd("gpt-4o", "openai", 0, 0));
    }

    [Fact]
    public void ComputeCostUsd_NegativeTokens_ClampedToZero()
    {
        Assert.Equal(0m, ModelPricing.ComputeCostUsd("gpt-4o", "openai", -5, -5));
    }

    // --- Formatting ------------------------------------------------------------------

    [Theory]
    [InlineData(0.0123, "$0.0123")]
    [InlineData(0.0, "$0.0000")]
    [InlineData(0.99999, "$1.0000")]
    [InlineData(1.2345, "$1.23")]
    [InlineData(123.456, "$123.46")]
    public void FormatUsd_UsesSensiblePrecision(double cost, string expected)
    {
        Assert.Equal(expected, ModelPricing.FormatUsd((decimal)cost));
    }

    [Fact]
    public void FormatUsd_NegativeClampedToZero()
    {
        Assert.Equal("$0.0000", ModelPricing.FormatUsd(-1m));
    }
}
