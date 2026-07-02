// Copyright (c) Andy Contributors
// Licensed under the MIT License.

// Integration tests for HeadlessToolHost's MCP routing contract (ADR 0002).
// They exercise BuildAsync against an in-process mock MCP server on loopback,
// covering gateway resolution, per-tool endpoint overrides, auth token
// forwarding, shared-endpoint deduplication, and configuration errors.

using System.Linq;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Cli.Services;
using Andy.Cli.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class HeadlessToolHostMcpRoutingIntegrationTests
{
    [Fact]
    public async Task BuildAsync_McpToolWithExplicitEndpoint_RegistersTool()
    {
        await using var server = new McpRoutingTestServer("my.tool");
        await server.StartAsync();

        var registry = new ToolRegistry();
        var tools = new List<HeadlessTool>
        {
            new() { Name = "my.tool", Transport = "mcp", Endpoint = server.BaseEndpoint },
        };

        await using var host = await BuildAsync(tools, registry, config: null);

        Assert.Single(registry.Tools);
        Assert.Equal("my.tool", registry.Tools[0].Metadata.Name);
    }

    [Fact]
    public async Task BuildAsync_McpGateway_ResolvesAndRoutesToolWithoutEndpoint()
    {
        await using var server = new McpRoutingTestServer("gateway.tool");
        await server.StartAsync();

        var registry = new ToolRegistry();
        var config = new HeadlessRunConfig
        {
            McpGateway = server.BaseEndpoint,
        };
        var tools = new List<HeadlessTool>
        {
            new() { Name = "gateway.tool", Transport = "mcp" },
        };

        await using var host = await BuildAsync(tools, registry, config);

        Assert.Single(registry.Tools);
        Assert.Equal("gateway.tool", registry.Tools[0].Metadata.Name);
    }

    [Fact]
    public async Task BuildAsync_McpGateway_SubstitutesAndyMcpUrlEnvVar()
    {
        await using var server = new McpRoutingTestServer("env.tool");
        await server.StartAsync();

        var original = Environment.GetEnvironmentVariable("ANDY_MCP_URL");
        try
        {
            Environment.SetEnvironmentVariable("ANDY_MCP_URL", server.BaseEndpoint);

            var registry = new ToolRegistry();
            var config = new HeadlessRunConfig
            {
                McpGateway = "$ANDY_MCP_URL",
            };
            var tools = new List<HeadlessTool>
            {
                new() { Name = "env.tool", Transport = "mcp" },
            };

            await using var host = await BuildAsync(tools, registry, config);

            Assert.Single(registry.Tools);
            Assert.Equal("env.tool", registry.Tools[0].Metadata.Name);
        }
        finally
        {
            RestoreEnvironmentVariable("ANDY_MCP_URL", original);
        }
    }

    [Fact]
    public async Task BuildAsync_AndyToken_ForwardedAsBearerOnEveryMcpRequest()
    {
        await using var server = new McpRoutingTestServer("secure.tool");
        await server.StartAsync();

        var original = Environment.GetEnvironmentVariable("ANDY_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("ANDY_TOKEN", "test-token-42");

            var registry = new ToolRegistry();
            var tools = new List<HeadlessTool>
            {
                new() { Name = "secure.tool", Transport = "mcp", Endpoint = server.BaseEndpoint },
            };

            await using var host = await BuildAsync(tools, registry, config: null);

            Assert.Single(registry.Tools);
            Assert.Contains(server.ReceivedAuthorizationHeaders, h => h == "Bearer test-token-42");
        }
        finally
        {
            RestoreEnvironmentVariable("ANDY_TOKEN", original);
        }
    }

    [Fact]
    public async Task BuildAsync_PerToolEndpoint_OverridesGateway()
    {
        await using var server = new McpRoutingTestServer("explicit.tool");
        await server.StartAsync();

        var registry = new ToolRegistry();
        var config = new HeadlessRunConfig
        {
            McpGateway = "http://should-not-be-used.invalid/mcp",
        };
        var tools = new List<HeadlessTool>
        {
            new() { Name = "explicit.tool", Transport = "mcp", Endpoint = server.BaseEndpoint },
        };

        await using var host = await BuildAsync(tools, registry, config);

        Assert.Single(registry.Tools);
        Assert.Equal("explicit.tool", registry.Tools[0].Metadata.Name);
    }

    [Fact]
    public async Task BuildAsync_MultipleMcpToolsOnSameEndpoint_SharedEndpoint_DeduplicatesConnection()
    {
        await using var server = new McpRoutingTestServer("tool.one", "tool.two");
        await server.StartAsync();

        var registry = new ToolRegistry();
        var tools = new List<HeadlessTool>
        {
            new() { Name = "tool.one", Transport = "mcp", Endpoint = server.BaseEndpoint },
            new() { Name = "tool.two", Transport = "mcp", Endpoint = server.BaseEndpoint },
        };

        await using var host = await BuildAsync(tools, registry, config: null);

        Assert.Equal(2, registry.Tools.Count);
        Assert.Contains(registry.Tools, t => t.Metadata.Name == "tool.one");
        Assert.Contains(registry.Tools, t => t.Metadata.Name == "tool.two");

        // One client per endpoint means one initialize handshake and one tools/list call.
        Assert.Equal(1, server.InitializeRequestCount);
        Assert.Equal(1, server.ToolsListRequestCount);
    }

    [Fact]
    public async Task BuildAsync_McpToolWithoutEndpointAndWithoutGateway_ThrowsInvalidOperation()
    {
        var registry = new ToolRegistry();
        var tools = new List<HeadlessTool>
        {
            new() { Name = "orphan.tool", Transport = "mcp" },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(tools, registry, config: null));

        Assert.Contains("orphan.tool", ex.Message);
        Assert.Contains("no endpoint", ex.Message);
        Assert.Contains("no mcp_gateway", ex.Message);
    }

    [Theory]
    [InlineData("not-a-valid-uri")]
    [InlineData("http://a b.com/mcp")]
    [InlineData("ftp://unsupported.scheme/mcp")]
    public async Task BuildAsync_InvalidEndpoint_Throws(string invalidEndpoint)
    {
        var registry = new ToolRegistry();
        var tools = new List<HeadlessTool>
        {
            new() { Name = "bad.tool", Transport = "mcp", Endpoint = invalidEndpoint },
        };

        var ex = await Record.ExceptionAsync(() => BuildAsync(tools, registry, config: null));

        Assert.NotNull(ex);
        Assert.True(
            ex is UriFormatException || ex is NotSupportedException,
            $"Expected {nameof(UriFormatException)} or {nameof(NotSupportedException)}, got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public async Task BuildAsync_AdvertisedToolNameMismatch_ThrowsInvalidOperation()
    {
        await using var server = new McpRoutingTestServer("wrong.tool");
        await server.StartAsync();

        var registry = new ToolRegistry();
        var tools = new List<HeadlessTool>
        {
            new() { Name = "expected.tool", Transport = "mcp", Endpoint = server.BaseEndpoint },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(tools, registry, config: null));

        Assert.Contains("expected.tool", ex.Message);
        Assert.Contains("wrong.tool", ex.Message);
    }

    private static async Task<HeadlessToolHost> BuildAsync(
        IReadOnlyList<HeadlessTool> tools,
        ToolRegistry registry,
        HeadlessRunConfig? config,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await HeadlessToolHost.BuildAsync(tools, registry, config, NullLoggerFactory.Instance, timeout.Token);
    }

    private static void RestoreEnvironmentVariable(string name, string? value)
    {
        if (value is null)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
        else
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
