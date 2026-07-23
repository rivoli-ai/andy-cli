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
    private readonly string _source;
    private readonly McpClient _client;
    private readonly Tool _remoteTool;
    private readonly ILogger<McpRemoteTool>? _logger;
    private readonly bool _usesArgumentEnvelope;

    public McpRemoteTool(
        HeadlessTool config,
        McpClient client,
        Tool remoteTool,
        ILogger<McpRemoteTool>? logger = null)
    {
        _source = config.Endpoint ?? "headless configuration";
        _client = client;
        _remoteTool = remoteTool;
        _logger = logger;
        var parameters = BuildParameters(remoteTool);
        _usesArgumentEnvelope = parameters.UsesEnvelope;
        Metadata = BuildMetadata(
            config.Name,
            config.Name,
            source: null,
            remoteTool,
            parameters.Items);
    }

    public McpRemoteTool(
        string toolId,
        string serverName,
        McpClient client,
        Tool remoteTool,
        ILogger<McpRemoteTool>? logger = null)
    {
        _source = serverName;
        _client = client;
        _remoteTool = remoteTool;
        _logger = logger;
        var parameters = BuildParameters(remoteTool);
        _usesArgumentEnvelope = parameters.UsesEnvelope;
        Metadata = BuildMetadata(toolId, toolId, serverName, remoteTool, parameters.Items);
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
        object? rawArgs = parameters;
        if (_usesArgumentEnvelope)
        {
            parameters.TryGetValue("arguments", out rawArgs);
        }

        _logger?.LogInformation(
            "McpRemoteTool[{Tool}]: calling remote {RemoteName} on {Source}",
            Metadata.Id, _remoteTool.Name, _source);

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

    private static ToolMetadata BuildMetadata(
        string id,
        string name,
        string? source,
        Tool remote,
        IList<ToolParameter> parameters) => new()
        {
            Id = id,
            Name = name,
            Description = (source is null ? string.Empty : $"[MCP: {source}] ")
            + (remote.Description ?? $"Remote MCP tool {remote.Name}."),
            Version = "1.0.0",
            Category = ToolCategory.Web,
            Parameters = parameters,
        };

    private static (IList<ToolParameter> Items, bool UsesEnvelope) BuildParameters(
        Tool remote)
    {
        if (remote.InputSchema.ValueKind == JsonValueKind.Object &&
            remote.InputSchema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            var required = ReadRequiredNames(remote.InputSchema);
            var result = new List<ToolParameter>();
            foreach (var property in properties.EnumerateObject())
            {
                var schema = property.Value;
                result.Add(new ToolParameter
                {
                    Name = property.Name,
                    Description = ReadString(schema, "description")
                        ?? $"Argument '{property.Name}' for the remote MCP tool.",
                    Type = ReadString(schema, "type") ?? "object",
                    Required = required.Contains(property.Name),
                    DefaultValue = ReadValue(schema, "default"),
                    AllowedValues = ReadAllowedValues(schema),
                    Format = ReadString(schema, "format"),
                });
            }

            if (result.Count > 0)
            {
                return (result, UsesEnvelope: false);
            }
        }

        return (
            new[]
            {
                new ToolParameter
                {
                    Name = "arguments",
                    Description = "Arguments object passed verbatim to the remote MCP tool's `arguments` field.",
                    Type = "object",
                    Required = false,
                },
            },
            UsesEnvelope: true);
    }

    private static HashSet<string> ReadRequiredNames(JsonElement schema)
    {
        if (!schema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return required.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? ReadString(JsonElement schema, string propertyName) =>
        schema.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static object? ReadValue(JsonElement schema, string propertyName) =>
        schema.TryGetProperty(propertyName, out var value)
            ? ConvertValue(value)
            : null;

    private static IList<object>? ReadAllowedValues(JsonElement schema)
    {
        if (!schema.TryGetProperty("enum", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return values.EnumerateArray()
            .Select(ConvertValue)
            .Where(value => value is not null)
            .Cast<object>()
            .ToArray();
    }

    private static object? ConvertValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.Null => null,
        _ => value.Clone(),
    };
}
