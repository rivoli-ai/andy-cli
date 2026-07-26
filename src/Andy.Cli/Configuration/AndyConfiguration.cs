using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Andy.Cli.Configuration;

/// <summary>
/// The typed view of the merged configuration. One instance is produced per process
/// by <see cref="AndyConfigurationService"/> and shared by interactive, headless and
/// ACP mode, so every entry point sees the same values.
///
/// Adding a section: add the property here, add the matching block to
/// schemas/andy-config.v1.json (closed with additionalProperties:false), document it
/// in docs/configuration.md and pin its precedence in
/// tests/Andy.Cli.Tests/Configuration. Nothing else needs to change — merge,
/// provenance, substitution, redaction and path resolution are schema-driven.
/// </summary>
public sealed class AndyConfiguration
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = ConfigSchema.Version;

    [JsonPropertyName("llm")]
    public LlmSection Llm { get; set; } = new();

    [JsonPropertyName("mcp")]
    public McpSection Mcp { get; set; } = new();

    [JsonPropertyName("ui")]
    public UiSection Ui { get; set; } = new();

    [JsonPropertyName("session")]
    public SessionSection Session { get; set; } = new();

    [JsonPropertyName("permissions")]
    public PermissionsSection Permissions { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingSection Logging { get; set; } = new();
}

/// <summary>Provider and model selection.</summary>
public sealed class LlmSection
{
    [JsonPropertyName("defaultProvider")]
    public string? DefaultProvider { get; set; }

    [JsonPropertyName("defaultModel")]
    public string? DefaultModel { get; set; }

    [JsonPropertyName("providers")]
    public Dictionary<string, LlmProviderSection> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One entry of <c>llm.providers</c>.</summary>
public sealed class LlmProviderSection
{
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("apiBase")]
    public string? ApiBase { get; set; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Interactive MCP servers.</summary>
public sealed class McpSection
{
    [JsonPropertyName("servers")]
    public Dictionary<string, McpServerSection> Servers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One entry of <c>mcp.servers</c>. Mirrors .andy/mcp-servers.json field for field.</summary>
public sealed class McpServerSection
{
    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

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
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Terminal UI preferences.</summary>
public sealed class UiSection
{
    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    [JsonPropertyName("transparentBackground")]
    public bool TransparentBackground { get; set; }

    /// <summary>"auto" (default), "unified" or "split". Mirrors ANDY_DIFF_STYLE.</summary>
    [JsonPropertyName("diffStyle")]
    public string DiffStyle { get; set; } = "auto";
}

/// <summary>Saved interactive sessions.</summary>
public sealed class SessionSection
{
    /// <summary>Absolute after loading; relative values are resolved against the declaring file.</summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; set; }

    /// <summary>Agent-loop safety valve. Mirrors ANDY_MAX_TURNS. Null keeps the built-in default.</summary>
    [JsonPropertyName("maxTurns")]
    public int? MaxTurns { get; set; }
}

/// <summary>
/// Tool-permission behaviour. The rules themselves stay in the dedicated
/// permissions.json security format; only the mode lives here. The resolved rule
/// file locations are exposed through <see cref="EffectiveConfiguration.ResolvedPaths"/>.
/// </summary>
public sealed class PermissionsSection
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "ask";

    /// <summary>True when tools that would normally prompt are auto-approved.</summary>
    [JsonIgnore]
    public bool AutoApprove => string.Equals(Mode, "auto", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Diagnostic logging.</summary>
public sealed class LoggingSection
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "warning";

    [JsonPropertyName("console")]
    public bool Console { get; set; }
}
