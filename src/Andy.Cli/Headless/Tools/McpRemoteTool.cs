using System.Text.Json;
using Andy.Cli.HeadlessConfig;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Headless.Tools;

// Adapter exposing a HeadlessTool with transport=mcp as an Andy.Tools ITool.
//
// The adapter holds a reference to a long-lived McpClient owned by the
// factory; one client is shared by every adapter targeting the same
// endpoint. ExecuteAsync delegates to McpClient.CallToolAsync, mapping
// the LLM's `arguments` parameter (a JSON-serializable object pass-through)
// straight to the protocol's Arguments field.
//
// The remote tool's `inputSchema` is intentionally NOT reflected into
// ToolMetadata.Parameters yet — the LLM is steered by the agent's
// instructions plus the tool's name+description, and the factory looks
// up the matching MCP Tool to populate Description from the server. A
// follow-on can map JSON Schema → ToolParameter for stronger LLM
// argument shaping.
public sealed class McpRemoteTool : ITool
{
    private readonly HeadlessTool _config;
    private readonly McpClient _client;
    private readonly Tool _remoteTool;
    private readonly ILogger<McpRemoteTool>? _logger;

    public McpRemoteTool(
        HeadlessTool config,
        McpClient client,
        Tool remoteTool,
        ILogger<McpRemoteTool>? logger = null)
    {
        _config = config;
        _client = client;
        _remoteTool = remoteTool;
        _logger = logger;
        Metadata = BuildMetadata(config, remoteTool);
    }

    public ToolMetadata Metadata { get; }

    public Task InitializeAsync(
        Dictionary<string, object?>? configuration = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IList<string> ValidateParameters(Dictionary<string, object?>? parameters) => Array.Empty<string>();

    public bool CanExecuteWithPermissions(ToolPermissions permissions) => true;

    public async Task<ToolResult> ExecuteAsync(
        Dictionary<string, object?> parameters,
        ToolExecutionContext context)
    {
        var ct = context.CancellationToken;
        parameters.TryGetValue("arguments", out var rawArgs);

        _logger?.LogInformation(
            "McpRemoteTool[{Tool}]: calling remote {RemoteName} on {Endpoint}",
            _config.Name, _remoteTool.Name, _config.Endpoint);

        CallToolResult result;
        try
        {
            result = await _client.CallToolAsync(_remoteTool.Name, rawArgs, ct);
        }
        catch (Exception ex)
        {
            return ToolResult.Failure(
                $"MCP call to {_remoteTool.Name} failed: {ex.GetType().Name}: {ex.Message}");
        }

        // CallToolResult.IsError carries the protocol-level "tool reported failure"
        // signal; transport errors throw and are caught above.
        var text = ExtractText(result);
        if (result.IsError == true)
        {
            return ToolResult.Failure(text);
        }
        return ToolResult.Success(text);
    }

    public Task DisposeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    // The MCP spec lets a tool return mixed content blocks (text, images,
    // resource refs). Headless agents only consume the textual portion
    // for now; non-text content is summarised by type so the LLM at
    // least knows it was elided.
    private static string ExtractText(CallToolResult result)
    {
        if (result.Content.Count == 0) return string.Empty;
        var parts = new List<string>(result.Content.Count);
        foreach (var c in result.Content)
        {
            switch (c)
            {
                case TextContent t:
                    parts.Add(t.Text);
                    break;
                default:
                    parts.Add($"[non-text content: {c.GetType().Name}]");
                    break;
            }
        }
        return string.Join("\n", parts);
    }

    private static ToolMetadata BuildMetadata(HeadlessTool config, Tool remote) => new()
    {
        Id = config.Name,
        Name = config.Name,
        Description = remote.Description ?? $"Remote MCP tool {remote.Name}.",
        Version = "1.0.0",
        Category = ToolCategory.Web,
        Parameters =
        [
            new ToolParameter
            {
                Name = "arguments",
                Description = "Arguments object passed verbatim to the remote MCP tool's `arguments` field.",
                Type = "object",
                Required = false,
            }
        ],
    };
}
