using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.Cli.Modes;

/// <summary>
/// Minimal, self-contained configuration for the mode system.
///
/// Plan mode fails closed, so tools it cannot classify - MCP tools above all, which carry no
/// capability metadata - are denied. This file is where a user records the opt-ins that re-enable
/// specific read-only tools, either one id at a time or for a whole MCP server. Nothing in this
/// file can WIDEN Plan mode beyond those opt-ins, no entry can re-enable a tool
/// <see cref="PlanModeToolPolicy"/> classifies as mutating, and there is no switch that turns the
/// overlay off.
///
/// On-disk shape (<c>.andy/modes.json</c> in the project, and the same path under the user's home
/// directory):
/// <code>
/// {
///   "planReadOnlyTools": ["mcp_docs_search"],
///   "planReadOnlyMcpServers": ["docs"],
///   "mcpPlanOptInAsked": { "docs": ["mcp_docs_search", "mcp_docs_fetch"] }
/// }
/// </code>
/// Both files are read and their opt-ins concatenated; a missing or malformed file contributes
/// nothing and is never an error (a broken config must not silently disable Plan mode, and it must
/// not break start-up either).
/// </summary>
public sealed class ModeConfigFile
{
    /// <summary>The conventional file name, used in both the project and user directories.</summary>
    public const string FileName = ".andy/modes.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Tool ids a user asserts are read-only, re-enabling them in Plan mode. Ids only; no
    /// wildcards, so an entry can never widen beyond the single tool it names.
    /// </summary>
    [JsonPropertyName("planReadOnlyTools")]
    public List<string> PlanReadOnlyTools { get; set; } = new();

    /// <summary>
    /// MCP server names granted server-wide. Unlike <see cref="PlanReadOnlyTools"/> this DOES cover
    /// tools the server exposes for the first time later - that is exactly what the server-wide
    /// choice means, and why it is recorded separately from the per-tool list.
    /// </summary>
    [JsonPropertyName("planReadOnlyMcpServers")]
    public List<string> PlanReadOnlyMcpServers { get; set; } = new();

    /// <summary>
    /// Which MCP tools the interactive opt-in offer has already been shown for, keyed by server
    /// name. Purely a "do not nag" record: it grants nothing. A server that later exposes a tool id
    /// absent from its list triggers a fresh offer, so newly discovered tools are surfaced rather
    /// than silently denied forever.
    /// </summary>
    [JsonPropertyName("mcpPlanOptInAsked")]
    public Dictionary<string, List<string>> McpPlanOptInAsked { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads one file, or an empty config when it is absent or unreadable.</summary>
    public static ModeConfigFile Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new ModeConfigFile();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<ModeConfigFile>(File.ReadAllText(path), ReadOptions)
                         ?? new ModeConfigFile();

            // Absent JSON members deserialize to null, not to the property initializer, so every
            // collection is re-established here; the rest of the code never null-checks them.
            loaded.PlanReadOnlyTools ??= new List<string>();
            loaded.PlanReadOnlyMcpServers ??= new List<string>();
            loaded.McpPlanOptInAsked = loaded.McpPlanOptInAsked is null
                ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(
                    loaded.McpPlanOptInAsked, StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch
        {
            // A malformed file contributes no opt-ins, which keeps Plan mode at its fail-closed
            // default rather than crashing or, worse, widening.
            return new ModeConfigFile();
        }
    }

    /// <summary>Persists to disk, creating the <c>.andy</c> directory when needed.</summary>
    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
    }

    /// <summary>
    /// Builds the Plan-mode policy for a workspace by merging the project and user config files.
    /// </summary>
    public static PlanModeToolPolicy LoadPolicy(string projectDirectory, string? userDirectory = null)
    {
        var tools = new List<string>();
        var servers = new List<string>();

        foreach (var config in new[]
                 {
                     Load(PathFor(projectDirectory)),
                     Load(PathFor(userDirectory ?? DefaultUserDirectory())),
                 })
        {
            tools.AddRange(config.PlanReadOnlyTools);
            servers.AddRange(config.PlanReadOnlyMcpServers);
        }

        return tools.Count == 0 && servers.Count == 0
            ? PlanModeToolPolicy.Default
            : new PlanModeToolPolicy(tools, servers);
    }

    /// <summary>The config path inside <paramref name="directory"/>.</summary>
    public static string PathFor(string directory) =>
        Path.Combine(directory ?? string.Empty, ".andy", "modes.json");

    /// <summary>The home directory whose <c>.andy/modes.json</c> holds user-scoped opt-ins.</summary>
    public static string DefaultUserDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
