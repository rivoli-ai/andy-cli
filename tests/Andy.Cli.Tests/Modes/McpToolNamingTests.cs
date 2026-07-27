using Andy.Cli.Modes;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// Server-wide Plan-mode grants match on the tool-id prefix the MCP host generates, so the two must
/// agree exactly. These lock that shared convention: if the host's id shape ever changed without
/// this helper changing with it, every server-wide grant would silently stop applying.
/// </summary>
public class McpToolNamingTests
{
    [Theory]
    [InlineData("docs", "search", "mcp_docs_search")]
    [InlineData("My Docs", "getIssue", "mcp_my_docs_getissue")]
    [InlineData("a-b.c", "x/y", "mcp_a_b_c_x_y")]
    public void ToolIdsMatchTheHostConvention(string server, string tool, string expected)
    {
        Assert.Equal(expected, McpToolNaming.BuildToolId(server, tool));
    }

    [Fact]
    public void TheHostAndTheGrantPolicyProduceIdenticalIds()
    {
        // Same input through the host's entry point and the shared helper.
        Assert.Equal(
            Andy.Cli.Mcp.InteractiveMcpToolHost.BuildToolId("My Docs", "search"),
            McpToolNaming.BuildToolId("My Docs", "search"));
    }

    [Fact]
    public void EveryToolIdForAServerStartsWithThatServersPrefix()
    {
        var prefix = McpToolNaming.ServerToolPrefix("My Docs");

        Assert.Equal("mcp_my_docs_", prefix);
        Assert.StartsWith(prefix, McpToolNaming.BuildToolId("My Docs", "anything"));
    }

    [Fact]
    public void EmptyNamesFallBackRatherThanProducingAnAmbiguousPrefix()
    {
        Assert.Equal("mcp_server_", McpToolNaming.ServerToolPrefix("!!!"));
        Assert.Equal("mcp_server_tool", McpToolNaming.BuildToolId("!!!", "???"));
    }

    [Fact]
    public void BelongsToServerMatchesTheUniquenessSuffixesTheHostAppends()
    {
        // The host disambiguates a collision as "<id>_2"; that still belongs to the same server.
        Assert.True(McpToolNaming.BelongsToServer("mcp_docs_search_2", "docs"));
        Assert.True(McpToolNaming.BelongsToServer("mcp_docs_search", "docs"));
        Assert.False(McpToolNaming.BelongsToServer("mcp_docsearch_x", "docs"));
        Assert.False(McpToolNaming.BelongsToServer("read_file", "docs"));
        Assert.False(McpToolNaming.BelongsToServer(null, "docs"));
    }
}
