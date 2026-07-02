// Copyright (c) Andy Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.Cli.Tests.TestHelpers;

/// <summary>
/// In-process mock MCP (Model Context Protocol) streamable HTTP server used to
/// exercise <see cref="HeadlessToolHost.BuildAsync"/> end-to-end without calling
/// external services. The server handles the minimal MCP 2025-06-18 handshake
/// (initialize, notifications/initialized, tools/list) and an SSE GET stream.
/// </summary>
internal sealed class McpRoutingTestServer : IAsyncDisposable
{
    private readonly List<string> _advertisedToolNames;
    private readonly List<string> _receivedAuthorizationHeaders = new();
    private readonly List<string> _receivedRequestPaths = new();
    private int _initializeRequestCount;
    private int _toolsListRequestCount;
    private WebApplication? _app;

    public McpRoutingTestServer(params string[] advertisedToolNames)
    {
        _advertisedToolNames = advertisedToolNames.ToList();
    }

    /// <summary>
    /// The base MCP endpoint exposed by the server, e.g. http://127.0.0.1:12345/mcp.
    /// Tools that configure this value as their <see cref="HeadlessTool.Endpoint"/>
    /// resolve to this path; gateway-routed tools resolve to {BaseEndpoint}/{tool.Name}.
    /// </summary>
    public string BaseEndpoint { get; private set; } = string.Empty;

    public IReadOnlyList<string> ReceivedAuthorizationHeaders => _receivedAuthorizationHeaders;

    public IReadOnlyList<string> ReceivedRequestPaths => _receivedRequestPaths;

    public int InitializeRequestCount => _initializeRequestCount;

    public int ToolsListRequestCount => _toolsListRequestCount;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // Catch /mcp and /mcp/{toolName} so gateway-derived endpoints work.
        _app.Map("/mcp/{**path}", async ctx =>
        {
            var path = ctx.Request.Path + ctx.Request.QueryString;
            lock (_receivedRequestPaths)
            {
                _receivedRequestPaths.Add($"{ctx.Request.Method} {path}");
            }

            if (ctx.Request.Headers.Authorization.Count > 0)
            {
                lock (_receivedAuthorizationHeaders)
                {
                    _receivedAuthorizationHeaders.Add(ctx.Request.Headers.Authorization.ToString());
                }
            }

            if (ctx.Request.Method == HttpMethods.Post)
            {
                await HandlePostAsync(ctx);
                return;
            }

            if (ctx.Request.Method == HttpMethods.Get)
            {
                await HandleSseGetAsync(ctx);
                return;
            }

            ctx.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
        });

        await _app.StartAsync(cancellationToken);

        var server = _app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Server addresses not available.");
        BaseEndpoint = addresses.First().TrimEnd('/') + "/mcp";
    }

    private async Task HandlePostAsync(HttpContext ctx)
    {
        var body = await new StreamReader(ctx.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false)
            .ReadToEndAsync(ctx.RequestAborted);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var method = root.GetProperty("method").GetString() ?? string.Empty;
        var hasId = root.TryGetProperty("id", out var id);

        if (method == "initialize")
        {
            Interlocked.Increment(ref _initializeRequestCount);
            ctx.Response.Headers["Mcp-Session-Id"] = "test-session";
            ctx.Response.ContentType = "application/json";

            var response = new
            {
                jsonrpc = "2.0",
                id = hasId ? id.GetInt32() : 0,
                result = new
                {
                    protocolVersion = "2025-06-18",
                    serverInfo = new { name = "test-mcp", version = "1.0" },
                    capabilities = new { tools = new object() },
                },
            };

            await ctx.Response.WriteAsync(JsonSerializer.Serialize(response));
            return;
        }

        if (method == "notifications/initialized")
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.Accepted;
            return;
        }

        if (method == "tools/list")
        {
            Interlocked.Increment(ref _toolsListRequestCount);
            ctx.Response.ContentType = "application/json";

            var response = new
            {
                jsonrpc = "2.0",
                id = hasId ? id.GetInt32() : 1,
                result = new
                {
                    tools = _advertisedToolNames.Select(n => new { name = n, inputSchema = new { type = "object" } }),
                },
            };

            await ctx.Response.WriteAsync(JsonSerializer.Serialize(response));
            return;
        }

        ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
    }

    private static async Task HandleSseGetAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";

        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            try
            {
                await ctx.Response.WriteAsync(": keep-alive\n\n");
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                await Task.Delay(TimeSpan.FromSeconds(1), ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            try
            {
                await _app.StopAsync();
            }
            catch
            {
                // Best-effort cleanup in the test dispose path.
            }

            await _app.DisposeAsync();
        }
    }
}
