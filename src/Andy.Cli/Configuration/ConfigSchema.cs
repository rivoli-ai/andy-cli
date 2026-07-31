using System;
using System.IO;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Andy.Cli.Configuration;

/// <summary>
/// Access to the versioned andy.jsonc schema.
///
/// The schema ships as an embedded resource (LogicalName pinned in Andy.Cli.csproj)
/// so a published single-file binary validates without the schemas/ directory being
/// present next to it — the same arrangement the headless config uses.
/// </summary>
public static class ConfigSchema
{
    /// <summary>The only version this build understands. Written into new files by the docs.</summary>
    public const int Version = 1;

    /// <summary>File name looked for at user and project scope.</summary>
    public const string FileName = "andy.jsonc";

    private const string EmbeddedName = "Andy.Cli.schemas.andy-config.v1.json";

    private static readonly Uri s_schemaId =
        new("https://rivoli-ai.com/schemas/andy-cli/andy-config.v1.json");

    private static readonly Lazy<string> s_text = new(ReadEmbedded);
    private static readonly Lazy<JsonObject> s_node = new(() =>
        JsonNode.Parse(s_text.Value) as JsonObject
        ?? throw new InvalidOperationException("The embedded andy-config schema is not a JSON object."));
    private static readonly Lazy<JsonSchema> s_schema = new(BuildSchema);

    /// <summary>The raw schema text (used by tests and by <c>config schema</c>).</summary>
    public static string Text => s_text.Value;

    /// <summary>The schema as a JSON tree, used for the unknown-key walk.</summary>
    public static JsonObject Node => s_node.Value;

    /// <summary>The compiled schema used for type, enum and range validation.</summary>
    public static JsonSchema Compiled => s_schema.Value;

    private static string ReadEmbedded()
    {
        var assembly = typeof(ConfigSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded config schema '{EmbeddedName}' not found. "
                    + "Check that Andy.Cli.csproj still embeds schemas/andy-config.v1.json.");
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static JsonSchema BuildSchema()
    {
        try
        {
            return JsonSchema.FromText(s_text.Value);
        }
        catch (Exception) when (SchemaRegistry.Global.Get(s_schemaId) is JsonSchema existing)
        {
            // JsonSchema.FromText auto-registers by $id and throws when the id is
            // already taken, which happens in a single test run where a fixture
            // loaded the same schema file from disk first. Content is identical by
            // construction, so reuse whichever instance won the race.
            return existing;
        }
    }
}
