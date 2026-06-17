using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class JsonRepairServiceTests
{
    private class Dto
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    private static JsonRepairService New() => new();

    // --- IsCompleteJson ---------------------------------------------------------

    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("[1,2,3]", true)]
    [InlineData("  {\"a\":[1,2]}  ", true)]
    [InlineData("{\"a\":\"}\"}", true)]   // closing brace inside a string must not count
    [InlineData("{\"a\":1", false)]        // unbalanced brace
    [InlineData("hello", false)]            // does not start with { or [
    [InlineData("", false)]
    public void IsCompleteJson_DetectsBalance(string json, bool expected)
        => Assert.Equal(expected, New().IsCompleteJson(json));

    // --- TryParseWithoutRepair --------------------------------------------------

    [Fact]
    public void TryParseWithoutRepair_ValidJson_ReturnsTrue()
    {
        Assert.True(New().TryParseWithoutRepair<Dto>("{\"name\":\"x\",\"value\":5}", out var dto));
        Assert.Equal("x", dto!.Name);
        Assert.Equal(5, dto.Value);
    }

    [Theory]
    [InlineData("{bad}")]
    [InlineData("")]
    public void TryParseWithoutRepair_InvalidOrEmpty_ReturnsFalse(string json)
        => Assert.False(New().TryParseWithoutRepair<Dto>(json, out _));

    // --- TryRepairJson ----------------------------------------------------------

    [Fact]
    public void TryRepairJson_FixesSingleQuotesAndUnquotedKeys()
    {
        Assert.True(New().TryRepairJson("{name: 'x'}", out var repaired));
        // Repaired output must be valid, parseable JSON carrying the key.
        Assert.True(New().IsCompleteJson(repaired));
        Assert.Contains("name", repaired);
    }

    [Fact]
    public void TryRepairJson_EmptyInput_ReturnsFalse()
        => Assert.False(New().TryRepairJson("", out _));

    // --- SafeParse --------------------------------------------------------------

    [Fact]
    public void SafeParse_RepairsUnquotedKeys()
    {
        var dto = New().SafeParse<Dto>("{name:\"x\",value:5}");
        Assert.NotNull(dto);
        Assert.Equal("x", dto!.Name);
        Assert.Equal(5, dto.Value);
    }

    [Fact]
    public void SafeParse_EmptyInput_ReturnsFallback()
    {
        var fallback = new Dto { Name = "fb" };
        Assert.Same(fallback, New().SafeParse("", fallback));
    }

    // --- ExtractToolCallJson ----------------------------------------------------

    [Fact]
    public void ExtractToolCallJson_FromToolCallTags()
        => Assert.Equal("{\"a\":1}", "<tool_call>{\"a\":1}</tool_call>".ExtractToolCallJson());

    [Fact]
    public void ExtractToolCallJson_FindsBalancedObjectInProse()
        => Assert.Equal("{\"a\":{\"b\":2}}", "blah {\"a\":{\"b\":2}} trailing".ExtractToolCallJson());

    [Fact]
    public void ExtractToolCallJson_NoJson_ReturnsNull()
        => Assert.Null("just some text".ExtractToolCallJson());

    [Fact]
    public void ExtractToolCallJson_UnbalancedObject_ReturnsRemainderFromBrace()
        => Assert.Equal("{\"a\":1", "prefix {\"a\":1".ExtractToolCallJson());

    // --- MinimalHeuristicRepair (internal) --------------------------------------

    [Fact]
    public void MinimalHeuristicRepair_QuotesKeysAndDropsTrailingCommas()
    {
        var repaired = JsonRepairExtensions.MinimalHeuristicRepair("{name: \"x\",}");
        Assert.Contains("\"name\"", repaired);
        Assert.DoesNotContain(",}", repaired.Replace(" ", ""));
    }
}
