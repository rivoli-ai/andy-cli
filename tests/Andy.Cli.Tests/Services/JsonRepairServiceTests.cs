using System.Text.Json;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class JsonRepairServiceTests
{
    private readonly JsonRepairService _service = new();

    [Fact]
    public void SafeParse_ValidJson_ReturnsObject()
    {
        // Arrange
        var json = """{"name": "test", "value": 123}""";

        // Act
        var result = _service.SafeParse<Dictionary<string, object?>>(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test", result["name"]?.ToString());
        
        // JsonElement needs special handling for numeric values
        if (result["value"] is JsonElement element)
        {
            Assert.Equal(123, element.GetInt32());
        }
        else
        {
            Assert.Equal(123L, Convert.ToInt64(result["value"]));
        }
    }

    [Fact]
    public void SafeParse_MalformedJson_ReturnsRepairedObject()
    {
        // Arrange - missing quotes and trailing comma
        var json = """{name: "test", value: 123,}""";

        // Act
        var result = _service.SafeParse<Dictionary<string, object?>>(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test", result["name"]?.ToString());
    }

    [Fact]
    public void SafeParse_CompletelyInvalidJson_ReturnsFallback()
    {
        // Arrange
        var json = "this is not json at all";
        var fallback = new Dictionary<string, object?> { ["error"] = "fallback" };

        // Act
        var result = _service.SafeParse(json, fallback);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("fallback", result["error"]?.ToString());
    }

    [Fact]
    public void IsCompleteJson_CompleteObject_ReturnsTrue()
    {
        // Arrange
        var json = """{"name": "test", "value": 123}""";

        // Act
        var result = _service.IsCompleteJson(json);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsCompleteJson_IncompleteObject_ReturnsFalse()
    {
        // Arrange
        var json = """{"name": "test", "value":""";

        // Act
        var result = _service.IsCompleteJson(json);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsCompleteJson_UnbalancedBraces_ReturnsFalse()
    {
        // Arrange
        var json = """{"name": {"nested": "value"}""";

        // Act
        var result = _service.IsCompleteJson(json);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TryRepairJson_MalformedJson_ReturnsTrue()
    {
        // Arrange
        var malformed = """{name: "test"}""";

        // Act
        var success = _service.TryRepairJson(malformed, out var repaired);

        // Assert
        Assert.True(success);
        Assert.NotEqual(malformed, repaired);
        
        // Should be parseable now
        var parsed = JsonDocument.Parse(repaired);
        Assert.Equal("test", parsed.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void ExtractToolCallJson_WithToolCallTags_ReturnsJson()
    {
        // Arrange
        var response = """
            I'll help you with that.
            <tool_call>
            {"name": "test_tool", "arguments": {"param": "value"}}
            </tool_call>
            Done!
            """;

        // Act
        var json = response.ExtractToolCallJson();

        // Assert
        Assert.NotNull(json);
        Assert.Contains("test_tool", json);
        Assert.Contains("arguments", json);
    }

    [Fact]
    public void ExtractToolCallJson_WithoutTags_ReturnsJsonObject()
    {
        // Arrange
        var response = """I need to call {"name": "tool", "arguments": {"x": 1}} for this task.""";

        // Act
        var json = response.ExtractToolCallJson();

        // Assert
        Assert.NotNull(json);
        Assert.Contains("name", json);
        Assert.Contains("tool", json);
    }

    [Fact]
    public void ExtractToolCallJson_NoJson_ReturnsNull()
    {
        // Arrange
        var response = "This response has no JSON at all.";

        // Act
        var json = response.ExtractToolCallJson();

        // Assert
        Assert.Null(json);
    }
}