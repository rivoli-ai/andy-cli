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
        counter.AddTokens(inputTokens: 9, outputTokens: 78);

        // Act
        var width = counter.GetWidth();

        // Assert - Format should be: "Total: 9→78 (87)"
        var expectedText = "Total: 9→78 (87)";
        Assert.Equal(expectedText.Length, width);
    }

    [Fact]
    public void TokenDisplay_InputTokensFirst_ThenOutputTokens()
    {
        // Arrange
        var counter = new TokenCounter();
        counter.AddTokens(inputTokens: 500, outputTokens: 100000);

        // Act
        var width = counter.GetWidth();

        // Assert - Format should show input (500) before output (100,000)
        // Expected: "Total: 500→100,000 (100,500)"
        var expectedText = "Total: 500→100,000 (100,500)";
        Assert.Equal(expectedText.Length, width);
    }

    [Fact]
    public void TokenDisplay_WithThousandsSeparators()
    {
        // Arrange
        var counter = new TokenCounter();
        counter.AddTokens(inputTokens: 89012, outputTokens: 1234567);

        // Act
        var width = counter.GetWidth();

        // Assert - Should format with thousands separators
        // Expected: "Total: 89,012→1,234,567 (1,323,579)"
        var expectedText = "Total: 89,012→1,234,567 (1,323,579)";
        Assert.Equal(expectedText.Length, width);
    }

    [Fact]
    public void AddTokens_AccumulatesCorrectly()
    {
        // Arrange
        var counter = new TokenCounter();

        // Act
        counter.AddTokens(inputTokens: 100, outputTokens: 1000);
        counter.AddTokens(inputTokens: 200, outputTokens: 2000);
        counter.AddTokens(inputTokens: 300, outputTokens: 3000);

        var width = counter.GetWidth();

        // Assert - Total should be 600 input + 6000 output = 6600
        // Expected: "Total: 600→6,000 (6,600)"
        var expectedText = "Total: 600→6,000 (6,600)";
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
