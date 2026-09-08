using System.Text.Json;
using Andy.Cli.Headless.Tools;
using Andy.MCP.Protocol;

namespace Andy.Cli.Tests.Mcp;

public sealed class McpRemoteToolResultTests
{
    [Fact]
    public void StructuredResult_PreservesJsonInToolData()
    {
        var remote = new CallToolResult
        {
            Content = [new TextContent("Summary")],
            StructuredContent = JsonSerializer.SerializeToElement(new { count = 2, items = new[] { "a", "b" } }),
        };

        var result = McpRemoteTool.MapResult(remote);

        Assert.True(result.IsSuccessful);
        var data = Assert.IsType<JsonElement>(result.Data);
        Assert.Equal(2, data.GetProperty("count").GetInt32());
        Assert.Equal("b", data.GetProperty("items")[1].GetString());
        Assert.Same(remote, result.Metadata["mcp_result"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedContentAndMetadata_ArePreservedOnSuccessAndError(bool isError)
    {
        var remote = JsonSerializer.Deserialize<CallToolResult>("""
            {"content":[{"type":"text","text":"description"},{"type":"image","data":"aGVsbG8=","mimeType":"image/png"}],
             "structuredContent":{"detail":"value"},"_meta":{"vendor":"trace"},"vendor/extra":42}
            """, McpJsonDefaults.Options)! with { IsError = isError };

        var result = McpRemoteTool.MapResult(remote);

        Assert.Equal(!isError, result.IsSuccessful);
        var retained = Assert.IsType<CallToolResult>(result.Metadata["mcp_result"]);
        Assert.Equal(2, retained.Content.Count);
        Assert.Equal("aGVsbG8=", Assert.IsType<ImageContent>(retained.Content[1]).Data);
        Assert.Equal("trace", retained.Meta!.Value.GetProperty("vendor").GetString());
        Assert.Equal(42, retained.ExtensionData!["vendor/extra"].GetInt32());
        if (isError) Assert.Contains("description", result.ErrorMessage);
    }

    [Fact]
    public void TextOnlyResult_KeepsExistingOutput()
    {
        var result = McpRemoteTool.MapResult(new CallToolResult
        {
            Content = [new TextContent("first"), new TextContent("second")],
        });

        Assert.Equal("first\nsecond", result.Data);
        Assert.Equal(string.Empty, McpRemoteTool.MapResult(new CallToolResult()).Data);
    }
}
