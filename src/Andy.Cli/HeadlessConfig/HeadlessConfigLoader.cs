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

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

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
            config = JsonSerializer.Deserialize<HeadlessRunConfig>(text, s_jsonOptions);
        }
        catch (JsonException ex)
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

        return HeadlessConfigLoadResult.Ok(config);
    }

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
