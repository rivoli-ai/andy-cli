using Andy.Cli.Headless.Tools;
using Andy.Cli.HeadlessConfig;
using Andy.MCP.Client;
using Andy.MCP.Configuration;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Headless;

// Builds the IToolRegistry the headless agent loop hands to SimpleAgent,
// turning the config's tools[] array into ITool adapters and managing the
// lifetime of any remote MCP sessions opened along the way.
//
// One McpClient is created per distinct endpoint URL (so adapters that
// share an endpoint reuse a single handshake/connection). All clients are
// disposed when the host itself is disposed at the end of the run.
//
// Auth: when the reserved ANDY_TOKEN env var is set (Epic Y6, run-scoped
// token lifecycle), it is attached as `Authorization: Bearer <token>` on
// every MCP request. The headless config's env_vars MUST NOT shadow this
// per the headless-runtime contract.
public sealed class HeadlessToolHost : IAsyncDisposable
{
    private readonly List<McpClient> _mcpClients = new();
    private readonly ILoggerFactory? _loggerFactory;

    public HeadlessToolHost(IToolRegistry registry, ILoggerFactory? loggerFactory = null)
    {
        Registry = registry;
        _loggerFactory = loggerFactory;
    }

    public IToolRegistry Registry { get; }

    public static async Task<HeadlessToolHost> BuildAsync(
        IReadOnlyList<HeadlessTool> tools,
        IToolRegistry registry,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        var host = new HeadlessToolHost(registry, loggerFactory);
        var logger = loggerFactory?.CreateLogger<HeadlessToolHost>();

        // One McpClient per distinct endpoint URL. The dictionary is keyed by
        // the raw endpoint string from the config — schema validation has
        // already ensured these are well-formed URIs.
        var mcpSessionsByEndpoint = new Dictionary<string, (McpClient Client, IReadOnlyList<Tool> RemoteTools)>(
            StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            switch (tool.Transport)
            {
                case "cli":
                {
                    var adapter = new CliSubprocessTool(tool, loggerFactory?.CreateLogger<CliSubprocessTool>());
                    Register(registry, adapter);
                    break;
                }
                case "mcp":
                {
                    if (string.IsNullOrEmpty(tool.Endpoint))
                    {
                        // Schema enforces endpoint on mcp transport; defensive guard
                        // surfaces a clearer message than a NRE deeper in.
                        throw new InvalidOperationException(
                            $"MCP tool '{tool.Name}' has no endpoint; schema validation should have rejected this.");
                    }

                    if (!mcpSessionsByEndpoint.TryGetValue(tool.Endpoint, out var session))
                    {
                        var client = await ConnectMcpAsync(tool.Endpoint, loggerFactory, ct);
                        host._mcpClients.Add(client);
                        var remoteTools = await client.ListToolsAsync(ct);
                        session = (client, remoteTools);
                        mcpSessionsByEndpoint[tool.Endpoint] = session;
                    }

                    var remote = session.RemoteTools.FirstOrDefault(t =>
                        string.Equals(t.Name, tool.Name, StringComparison.Ordinal));
                    if (remote is null)
                    {
                        // The configurator (Epic AP3) and the agent registry (Epic W)
                        // are supposed to agree on tool names; surface a hard error
                        // here so the operator sees the mismatch instead of the LLM
                        // silently never calling the tool.
                        throw new InvalidOperationException(
                            $"MCP endpoint {tool.Endpoint} does not advertise a tool named '{tool.Name}'. "
                                + $"Available: [{string.Join(", ", session.RemoteTools.Select(t => t.Name))}]");
                    }

                    var adapter = new McpRemoteTool(
                        tool, session.Client, remote,
                        loggerFactory?.CreateLogger<McpRemoteTool>());
                    Register(registry, adapter);
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Unsupported transport '{tool.Transport}' on tool '{tool.Name}'.");
            }
        }

        logger?.LogInformation(
            "HeadlessToolHost: registered {ToolCount} tool(s) across {EndpointCount} MCP endpoint(s)",
            tools.Count, mcpSessionsByEndpoint.Count);

        return host;
    }

    private static async Task<McpClient> ConnectMcpAsync(
        string endpoint,
        ILoggerFactory? loggerFactory,
        CancellationToken ct)
    {
        var transportOptions = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint),
            AdditionalHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        // Y6 run-scoped token: forward as bearer if present. The config can't
        // override this — it's injected by the container runtime.
        var token = Environment.GetEnvironmentVariable("ANDY_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            transportOptions.AdditionalHeaders["Authorization"] = $"Bearer {token}";
        }

        var transport = new StreamableHttpClientTransport(
            transportOptions,
            loggerFactory?.CreateLogger<StreamableHttpClientTransport>());

        return await McpClient.ConnectAsync(
            transport,
            options: null,
            logger: loggerFactory?.CreateLogger<McpClient>(),
            cancellationToken: ct);
    }

    // The IToolRegistry instance overload takes a ToolMetadata + factory
    // closure; this lets us register a per-config adapter instance rather
    // than going through DI activation of a tool Type.
    private static void Register(IToolRegistry registry, ITool tool)
    {
        registry.RegisterTool(tool.Metadata, _ => tool, configuration: new Dictionary<string, object?>());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _mcpClients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch
            {
                // Cleanup path — swallow so one bad endpoint doesn't mask a
                // higher-priority error already in flight.
            }
        }
        _mcpClients.Clear();
    }
}
