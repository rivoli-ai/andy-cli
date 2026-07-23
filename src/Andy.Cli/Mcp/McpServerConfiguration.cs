using System.Text.Json.Serialization;

namespace Andy.Cli.Mcp;

/// <summary>
/// Configuration for one interactive MCP server.
/// </summary>
public sealed class McpServerConfiguration
{
    [JsonIgnore]
    public string Name { get; internal set; } = string.Empty;

    [JsonPropertyName("transport")]
    public string Transport { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string> Environment { get; set; } =
        new(StringComparer.Ordinal);

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class McpServerConfigurationFile
{
    [JsonPropertyName("servers")]
    public Dictionary<string, McpServerConfiguration> Servers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Result of loading MCP configuration from all supported sources.
/// </summary>
public sealed record McpConfigurationLoadResult(
    IReadOnlyList<McpServerConfiguration> Servers,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Sources);
