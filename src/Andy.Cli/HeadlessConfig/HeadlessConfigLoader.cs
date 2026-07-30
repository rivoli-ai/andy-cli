using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Andy.Cli.HeadlessConfig;

// Loads and validates a headless run config file against the AQ1 schema
// (embedded at build time from schemas/headless-config.v1.json). Returns a
// parsed HeadlessRunConfig on success or a human-readable error string on
// any failure — the caller (HeadlessRunner) maps failures to
// HeadlessExitCode.ConfigError so the exit-code contract stays in one
// place.
public static class HeadlessConfigLoader
{
    // The schema ships embedded (not read from disk at runtime) so a
    // published binary doesn't depend on the schemas/ directory being
    // present alongside it. LogicalName is pinned in the .csproj.
    private const string EmbeddedSchemaName = "Andy.Cli.schemas.headless-config.v1.json";

    private static readonly Lazy<JsonSchema> s_schema = new(LoadEmbeddedSchema);

    public static async Task<HeadlessConfigLoadResult> TryLoadAsync(
        string path,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return HeadlessConfigLoadResult.Fail("Config path is empty.");
        }

        if (!File.Exists(path))
        {
            return HeadlessConfigLoadResult.Fail($"Config file not found: {path}");
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct);
        }
        catch (IOException ex)
        {
            return HeadlessConfigLoadResult.Fail($"Failed to read config file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return HeadlessConfigLoadResult.Fail($"Permission denied reading config: {ex.Message}");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            return HeadlessConfigLoadResult.Fail($"Config is not valid JSON: {ex.Message}");
        }
        if (node is null)
        {
            return HeadlessConfigLoadResult.Fail("Config parsed to null (empty document).");
        }

        // rivoli-ai/andy-cli#180: policy_id and boundaries were removed from v1
        // because the runtime enforced neither and their presence created false
        // security assurance. The schema now rejects them as unknown properties,
        // but that produces a cryptic additionalProperties error; detect them here
        // first so the operator gets an actionable message.
        if (node is JsonObject rootObj)
        {
            foreach (var removed in RemovedFields)
            {
                if (rootObj.ContainsKey(removed))
                {
                    return HeadlessConfigLoadResult.Fail(
                        $"Config field '{removed}' is not supported in headless-config.v1. It was "
                            + "removed because the runtime enforced no such control, so it created "
                            + "false security assurance. Remove it. Use permissions.allowed_tools for "
                            + "enforceable per-run tool controls. See docs/adr/0002-headless-v1-inactive-fields.md.");
                }
            }
        }

        // Schema validation must happen against a JsonElement tree; JsonSchema.Net
        // 9.x's primary overload is element-based. Convert once so error formatting
        // and deserialization can share the same underlying bytes.
        var element = JsonDocument.Parse(node.ToJsonString()).RootElement;
        var result = s_schema.Value.Evaluate(element, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (!result.IsValid)
        {
            return HeadlessConfigLoadResult.Fail(
                "Config does not match headless-config.v1 schema:"
                    + Environment.NewLine
                    + FormatSchemaErrors(result));
        }

        HeadlessRunConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(
                text,
                HeadlessConfigJsonContext.Default.HeadlessRunConfig);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // The schema should have caught structural problems already; a failure
            // here typically means a type-level mismatch (e.g. unparseable Guid) that
            // the schema allows but System.Text.Json can't coerce.
            return HeadlessConfigLoadResult.Fail(
                $"Config passed schema validation but failed to deserialize: {ex.Message}");
        }

        if (config is null)
        {
            return HeadlessConfigLoadResult.Fail("Config deserialization returned null.");
        }

        config = NormalizeDefaults(config);

        // rivoli-ai/andy-cli#180: cross-field and runtime-support checks the JSON
        // Schema cannot express (api_key_ref scheme, reserved env-var protection,
        // FIFO-requires-path). Fail fast with a clear, secret-free message.
        var semanticError = HeadlessConfigValidator.Validate(config);
        if (semanticError is not null)
        {
            return HeadlessConfigLoadResult.Fail(semanticError);
        }

        return HeadlessConfigLoadResult.Ok(config);
    }

    private static HeadlessRunConfig NormalizeDefaults(HeadlessRunConfig config)
    {
        // The source generator initializes every init-only property in one object
        // initializer. An omitted JSON member therefore supplies default(T) and
        // overwrites the model's property initializer. Restore schema defaults at
        // this boundary; the schema has already rejected explicit zero/null values.
        var transcriptDefaults = new HeadlessTranscript();
        var transcript = config.Transcript is null
            ? null
            : config.Transcript with
            {
                MaxRecordBytes = config.Transcript.MaxRecordBytes == 0
                    ? transcriptDefaults.MaxRecordBytes
                    : config.Transcript.MaxRecordBytes,
                MaxRunBytes = config.Transcript.MaxRunBytes == 0
                    ? transcriptDefaults.MaxRunBytes
                    : config.Transcript.MaxRunBytes,
                MaxAgeDays = config.Transcript.MaxAgeDays == 0
                    ? transcriptDefaults.MaxAgeDays
                    : config.Transcript.MaxAgeDays,
                MaxFiles = config.Transcript.MaxFiles == 0
                    ? transcriptDefaults.MaxFiles
                    : config.Transcript.MaxFiles,
                MaxTotalBytes = config.Transcript.MaxTotalBytes == 0
                    ? transcriptDefaults.MaxTotalBytes
                    : config.Transcript.MaxTotalBytes,
                RedactEnvVars = config.Transcript.RedactEnvVars ?? [],
            };

        var requiredActions = (config.RequiredActions ?? [])
            .Select(requirement => requirement.AtLeast == 0
                ? requirement with { AtLeast = 1 }
                : requirement)
            .ToArray();

        return config with
        {
            Transcript = transcript,
            Permissions = config.Permissions is null
                ? null
                : config.Permissions with
                {
                    AllowedTools = config.Permissions.AllowedTools ?? [],
                },
            RequiredActions = requiredActions,
        };
    }

    // Fields that were part of an earlier v1 draft but are rejected now. Kept as a
    // named set so the loader can emit a targeted message instead of a generic
    // additionalProperties schema error.
    private static readonly string[] RemovedFields = { "policy_id", "boundaries" };

    // Schema $id declared in schemas/headless-config.v1.json. Kept as a local
    // constant so the registry-collision fallback below doesn't re-parse the
    // file to discover it.
    private static readonly Uri SchemaId = new("https://rivoli-ai.com/schemas/andy-cli/headless-config.v1.json");

    private static JsonSchema LoadEmbeddedSchema()
    {
        var assembly = typeof(HeadlessConfigLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedSchemaName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded headless-config schema '{EmbeddedSchemaName}' not found. "
                    + "Check that Andy.Cli.csproj still embeds schemas/headless-config.v1.json.");
        }
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        try
        {
            return JsonSchema.FromText(text);
        }
        catch (Exception) when (SchemaRegistry.Global.Get(SchemaId) is JsonSchema existing)
        {
            // JsonSchema.FromText auto-registers against the process-global
            // SchemaRegistry by $id and throws (JsonSchemaException, not the
            // ArgumentException one might expect) when the $id is already
            // taken. That happens in a single test run where AQ1's schema
            // fixtures already called FromFile/FromText on the same file.
            // Reuse whichever instance got there first — content is identical
            // by construction.
            return existing;
        }
    }

    private static string FormatSchemaErrors(EvaluationResults results)
    {
        var writer = new StringWriter();
        if (results.Details is null)
        {
            return results.IsValid ? "(valid)" : "(invalid, no details available)";
        }
        foreach (var detail in results.Details)
        {
            if (detail.IsValid) continue;
            var errors = detail.Errors is null
                ? "(no errors)"
                : string.Join("; ", detail.Errors.Select(kv => $"{kv.Key}={kv.Value}"));
            writer.WriteLine($"  {detail.EvaluationPath}: {errors}");
        }
        return writer.ToString();
    }
}

public sealed record HeadlessConfigLoadResult
{
    public HeadlessRunConfig? Config { get; init; }
    public string? Error { get; init; }

    public bool IsSuccess => Config is not null && Error is null;

    public static HeadlessConfigLoadResult Ok(HeadlessRunConfig config) =>
        new() { Config = config };

    public static HeadlessConfigLoadResult Fail(string error) =>
        new() { Error = error };
}
