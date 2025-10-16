using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class TokenCounterTests
{
    [Fact]
    public void GetWidth_WithFormattedNumbers_ReturnsCorrectLength()
    {
        // Arrange
        var counter = new TokenCounter();
        counter.AddTokens(inputTokens: 512493, outputTokens: 638);

        // Act
        var width = counter.GetWidth();

        // Assert - Format should be: "Total: 512,493→638 (513,131)"
        var expectedText = "Total: 512,493→638 (513,131)";
        Assert.Equal(expectedText.Length, width);
    }

    [Fact]
    public void TokenDisplay_InputTokensFirst_ThenOutputTokens()
    {
        // Arrange
        var counter = new TokenCounter();
        counter.AddTokens(inputTokens: 100000, outputTokens: 500);

        // Act
        var width = counter.GetWidth();

        // Assert - Format should show input (100,000) before output (500)
        // Expected: "Total: 100,000→500 (100,500)"
        var expectedText = "Total: 100,000→500 (100,500)";
        Assert.Equal(expectedText.Length, width);
    }

    [Fact]
    public void TokenDisplay_WithThousandsSeparators()
    {
        // Arrange
        var counter = new TokenCounter();
        counter.AddTokens(inputTokens: 1234567, outputTokens: 89012);

        // Act
        var width = counter.GetWidth();

        // Assert - Should format with thousands separators
        // Expected: "Total: 1,234,567→89,012 (1,323,579)"
        var expectedText = "Total: 1,234,567→89,012 (1,323,579)";
        Assert.Equal(expectedText.Length, width);
    }

    [Fact]
    public void AddTokens_AccumulatesCorrectly()
    {
        // Arrange
        var counter = new TokenCounter();

        // Act
        counter.AddTokens(inputTokens: 1000, outputTokens: 100);
        counter.AddTokens(inputTokens: 2000, outputTokens: 200);
        counter.AddTokens(inputTokens: 3000, outputTokens: 300);

        var width = counter.GetWidth();

        // Assert - Total should be 6000 input + 600 output = 6600
        // Expected: "Total: 6,000→600 (6,600)"
        var expectedText = "Total: 6,000→600 (6,600)";
        Assert.Equal(expectedText.Length, width);
    }

    [Fact]
    public void Reset_ClearsAllTokens()
    {
        // Arrange
        var counter = new TokenCounter();
        counter.AddTokens(inputTokens: 5000, outputTokens: 500);

        // Act
        counter.Reset();
        var width = counter.GetWidth();

        // Assert - Should show zeros
        // Expected: "Total: 0→0 (0)"
        var expectedText = "Total: 0→0 (0)";
        Assert.Equal(expectedText.Length, width);
    }
}
