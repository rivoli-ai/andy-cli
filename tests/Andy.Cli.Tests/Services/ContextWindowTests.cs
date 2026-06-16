using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class ContextWindowTests
{
    [Theory]
    [InlineData("claude-opus-4", "anthropic", 200_000)]
    [InlineData("anything", "anthropic", 200_000)]
    [InlineData("openai/gpt-4o", "openai", 128_000)]
    [InlineData("gpt-4.1", "openai", 128_000)]
    [InlineData("gpt-4-turbo", "openai", 128_000)]
    [InlineData("o1-preview", "openai", 128_000)]
    [InlineData("gpt-4", "openai", 8_192)]
    [InlineData("gpt-3.5-turbo", "openai", 16_385)]
    [InlineData("gemini-1.5-pro", "google", 1_000_000)]
    [InlineData("llama-3.3-70b", "cerebras", 8_192)]
    public void GetMaxTokens_KnownModels(string model, string provider, int expected)
    {
        Assert.Equal(expected, ContextWindow.GetMaxTokens(model, provider));
    }

    [Theory]
    [InlineData("some-unknown-model", "mystery")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void GetMaxTokens_UnknownReturnsZero(string? model, string? provider)
    {
        Assert.Equal(0, ContextWindow.GetMaxTokens(model, provider));
    }

    [Fact]
    public void GetMaxTokens_IsCaseInsensitive()
    {
        Assert.Equal(200_000, ContextWindow.GetMaxTokens("CLAUDE-OPUS", "ANTHROPIC"));
    }
}
