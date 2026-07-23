using System.Text;
using Andy.Cli.Headless.Tools;
using Andy.MCP.Client;
using Andy.MCP.Transport;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Mcp;

public enum McpServerConnectionState
{
    Disabled,
    Connected,
    Failed,
}

public sealed record McpServerStatus(
    string Name,
    string Transport,
    McpServerConnectionState State,
    IReadOnlyList<string> ToolIds,
    string? Detail = null);

/// <summary>
/// Owns interactive MCP client sessions and registers their remote tools in
/// the same registry as Andy's built-in tools.
/// </summary>
public sealed class InteractiveMcpToolHost : IAsyncDisposable
{
    private readonly IToolRegistry _registry;
    private readonly List<McpClient> _clients = new();
    private readonly List<string> _registeredToolIds = new();
    private readonly List<McpServerStatus> _statuses = new();

    private InteractiveMcpToolHost(
        IToolRegistry registry,
        McpConfigurationLoadResult configuration)
    {
        _registry = registry;
        Configuration = configuration;
    }

    public McpConfigurationLoadResult Configuration { get; }

    public IReadOnlyList<McpServerStatus> Statuses => _statuses;

    public static async Task<InteractiveMcpToolHost> BuildAsync(
        McpConfigurationLoadResult configuration,
        IToolRegistry registry,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        return await BuildWithTransportFactoryAsync(
            configuration,
            registry,
            server => BuildTransport(server, loggerFactory),
            loggerFactory,
            cancellationToken);
    }

    internal static async Task<InteractiveMcpToolHost> BuildWithTransportFactoryAsync(
        McpConfigurationLoadResult configuration,
        IToolRegistry registry,
        Func<McpServerConfiguration, IClientTransport> transportFactory,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        var host = new InteractiveMcpToolHost(registry, configuration);
        var logger = loggerFactory?.CreateLogger<InteractiveMcpToolHost>();

        foreach (var server in configuration.Servers)
        {
            if (!server.Enabled)
            {
                host._statuses.Add(new McpServerStatus(
                    server.Name,
                    server.Transport,
                    McpServerConnectionState.Disabled,
                    Array.Empty<string>()));
                continue;
            }

            McpClient? client = null;
            var toolIds = new List<string>();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                var transport = transportFactory(server);
                client = await McpClient.ConnectAsync(
                    transport,
                    options: null,
                    logger: loggerFactory?.CreateLogger<McpClient>(),
                    cancellationToken: timeout.Token);

                var tools = await client.ListToolsAsync(timeout.Token);
                foreach (var remoteTool in tools)
                {
                    var toolId = host.CreateUniqueToolId(server.Name, remoteTool.Name);
                    var adapter = new McpRemoteTool(
                        toolId,
                        server.Name,
                        client,
                        remoteTool,
                        loggerFactory?.CreateLogger<McpRemoteTool>());
                    Register(registry, adapter);
                    host._registeredToolIds.Add(toolId);
                    toolIds.Add(toolId);
                }

                host._clients.Add(client);
                client = null;
                host._statuses.Add(new McpServerStatus(
                    server.Name,
                    server.Transport,
                    McpServerConnectionState.Connected,
                    toolIds));
                logger?.LogInformation(
                    "Connected MCP server {ServerName} via {Transport}; registered {ToolCount} tools",
                    server.Name,
                    server.Transport,
                    toolIds.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                }
                foreach (var toolId in toolIds)
                {
                    registry.UnregisterTool(toolId);
                    host._registeredToolIds.Remove(toolId);
                }
                await host.DisposeAsync();
                throw;
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                }
                foreach (var toolId in toolIds)
                {
                    registry.UnregisterTool(toolId);
                    host._registeredToolIds.Remove(toolId);
                }

                host._statuses.Add(new McpServerStatus(
                    server.Name,
                    server.Transport,
                    McpServerConnectionState.Failed,
                    Array.Empty<string>(),
                    $"Connection failed ({ex.GetType().Name})."));
                logger?.LogWarning(ex, "Could not connect MCP server {ServerName}", server.Name);
            }
        }

        return host;
    }

    internal static string BuildArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteArgument));

    internal static string BuildToolId(string serverName, string remoteToolName)
    {
        var serverId = NormalizeIdentifier(serverName);
        var toolId = NormalizeIdentifier(remoteToolName);
        return $"mcp_{(serverId.Length == 0 ? "server" : serverId)}_"
            + (toolId.Length == 0 ? "tool" : toolId);
    }

    private static IClientTransport BuildTransport(
        McpServerConfiguration server,
        ILoggerFactory? loggerFactory)
    {
        return server.Transport.Trim().ToLowerInvariant() switch
        {
            "stdio" => new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = server.Command!,
                    Arguments = BuildArguments(server.Args),
                    WorkingDirectory = server.WorkingDirectory,
                    EnvironmentVariables = server.Environment,
                },
                loggerFactory?.CreateLogger<StdioClientTransport>()),
            "http" or "sse" or "streamable-http" => new StreamableHttpClientTransport(
                new StreamableHttpClientTransportOptions
                {
                    Endpoint = new Uri(server.Url!),
                    AdditionalHeaders = server.Headers,
                },
                loggerFactory?.CreateLogger<StreamableHttpClientTransport>()),
            _ => throw new InvalidOperationException(
                $"Unsupported MCP transport '{server.Transport}'."),
        };
    }

    private string CreateUniqueToolId(string serverName, string remoteToolName)
    {
        var baseId = BuildToolId(serverName, remoteToolName);
        var candidate = baseId;
        var suffix = 2;
        while (_registry.GetTool(candidate) is not null ||
               _registeredToolIds.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            candidate = $"{baseId}_{suffix++}";
        }
        return candidate;
    }

    private static string NormalizeIdentifier(string value)
    {
        var result = new StringBuilder(value.Length);
        var previousUnderscore = false;
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
                previousUnderscore = false;
            }
            else if (!previousUnderscore)
            {
                result.Append('_');
                previousUnderscore = true;
            }
        }

        return result.ToString().Trim('_');
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 &&
            !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static void Register(IToolRegistry registry, ITool tool)
    {
        registry.RegisterTool(
            tool.Metadata,
            _ => tool,
            configuration: new Dictionary<string, object?>());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var toolId in _registeredToolIds)
        {
            _registry.UnregisterTool(toolId);
        }
        _registeredToolIds.Clear();

        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch
            {
                // Best-effort shutdown. One failed session must not prevent the
                // remaining MCP subprocesses/connections from being released.
            }
        }
        _clients.Clear();
    }
}
