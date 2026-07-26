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
/// capability metadata - are denied. An operator who knows a specific tool is read-only can say so
/// here; nothing else in this file can WIDEN Plan mode beyond that list, and there is no switch
/// that turns the overlay off.
///
/// On-disk shape (<c>.andy/modes.json</c> in the project, and the same file under the user's home
/// <c>.andy</c> directory):
/// <code>{ "planReadOnlyTools": ["mcp__docs__search", "mcp__jira__get_issue"] }</code>
/// Both files are read and their lists concatenated; a missing or malformed file contributes
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

    /// <summary>
    /// Tool ids an operator asserts are read-only, re-enabling them in Plan mode. Ids only; no
    /// wildcards, so an entry can never widen beyond the single tool it names.
    /// </summary>
    [JsonPropertyName("planReadOnlyTools")]
    public List<string> PlanReadOnlyTools { get; set; } = new();

    /// <summary>Loads one file, or an empty config when it is absent or unreadable.</summary>
    public static ModeConfigFile Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new ModeConfigFile();
        }

        try
        {
            return JsonSerializer.Deserialize<ModeConfigFile>(File.ReadAllText(path), ReadOptions)
                   ?? new ModeConfigFile();
        }
        catch
        {
            // A malformed file contributes no opt-ins, which keeps Plan mode at its fail-closed
            // default rather than crashing or, worse, widening.
            return new ModeConfigFile();
        }
    }

    /// <summary>
    /// Builds the Plan-mode policy for a workspace by merging the project and user config files.
    /// </summary>
    public static PlanModeToolPolicy LoadPolicy(string projectDirectory, string? userDirectory = null)
    {
        var tools = new List<string>();
        tools.AddRange(Load(PathFor(projectDirectory)).PlanReadOnlyTools);
        tools.AddRange(Load(PathFor(
            userDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))).PlanReadOnlyTools);
        return tools.Count == 0 ? PlanModeToolPolicy.Default : new PlanModeToolPolicy(tools);
    }

    /// <summary>The config path inside <paramref name="directory"/>.</summary>
    public static string PathFor(string directory) =>
        Path.Combine(directory ?? string.Empty, ".andy", "modes.json");
}
