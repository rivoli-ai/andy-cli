using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Andy.Cli.Headless.Contract;

/// <summary>
/// Validates headless run configurations and event-stream records against the
/// canonical Andy CLI headless JSON schemas.
/// </summary>
/// <remarks>
/// Generators such as andy-containers and conductor can call
/// <see cref="ValidateConfig(string)"/> to confirm a produced configuration matches
/// the contract before launching <c>andy-cli run --headless</c>, without depending on
/// the full CLI. The schemas are embedded in this assembly so there is a single
/// source of truth shared with the repository's <c>schemas/</c> directory.
/// </remarks>
public static class HeadlessConfigContract
{
    /// <summary>
    /// The schema version this contract validates. Matches the required
    /// <c>version</c> constant in the configuration schema.
    /// </summary>
    public const int SchemaVersion = 1;

    private const string ConfigSchemaResource = "Andy.Cli.Headless.Contract.headless-config.v1.json";
    private const string EventSchemaResource = "Andy.Cli.Headless.Contract.headless-events.v1.json";

    private static readonly Lazy<string> ConfigSchemaTextLazy = new(() => ReadEmbeddedResource(ConfigSchemaResource));
    private static readonly Lazy<string> EventSchemaTextLazy = new(() => ReadEmbeddedResource(EventSchemaResource));
    private static readonly Lazy<JsonSchema> ConfigSchemaLazy = new(() => ParseSchema(ConfigSchemaTextLazy.Value));
    private static readonly Lazy<JsonSchema> EventSchemaLazy = new(() => ParseSchema(EventSchemaTextLazy.Value));

    private static JsonSchema ParseSchema(string text)
    {
        // JsonSchema.Net registers any schema carrying an "$id" into a process-global
        // SchemaRegistry and throws "Overwriting registered schemas is not permitted"
        // if another component (e.g. the andy-cli test suite's own schema loader)
        // registers the same $id. These schemas contain no internal "$ref", so the
        // $id is unused for resolution here; strip it before building so evaluation
        // stays isolated and never collides with another registrant.
        var node = JsonNode.Parse(text)
            ?? throw new InvalidOperationException("Embedded schema text parsed to null.");
        if (node is JsonObject obj)
        {
            obj.Remove("$id");
        }

        return JsonSerializer.Deserialize<JsonSchema>(node)
            ?? throw new InvalidOperationException("Embedded schema text deserialized to null.");
    }

    /// <summary>
    /// Gets the raw JSON text of the embedded headless configuration schema.
    /// </summary>
    /// <returns>The configuration schema document as JSON text.</returns>
    public static string GetConfigSchemaText() => ConfigSchemaTextLazy.Value;

    /// <summary>
    /// Gets the raw JSON text of the embedded headless event-stream schema.
    /// </summary>
    /// <returns>The event schema document as JSON text.</returns>
    public static string GetEventSchemaText() => EventSchemaTextLazy.Value;

    /// <summary>
    /// Validates a headless configuration document supplied as JSON text.
    /// </summary>
    /// <param name="json">The configuration document, serialized as JSON.</param>
    /// <returns>
    /// A <see cref="ValidationResult"/> indicating whether the document satisfies the
    /// configuration schema, along with any error messages.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <c>null</c>.</exception>
    public static ValidationResult ValidateConfig(string json)
        => ValidateText(json, ConfigSchemaLazy.Value);

    /// <summary>
    /// Validates a headless configuration document supplied as a <see cref="JsonElement"/>.
    /// </summary>
    /// <param name="config">The configuration document.</param>
    /// <returns>
    /// A <see cref="ValidationResult"/> indicating whether the document satisfies the
    /// configuration schema, along with any error messages.
    /// </returns>
    public static ValidationResult ValidateConfig(JsonElement config)
        => ValidateElement(config, ConfigSchemaLazy.Value);

    /// <summary>
    /// Validates a single headless event record supplied as JSON text.
    /// </summary>
    /// <param name="json">The event record, serialized as JSON.</param>
    /// <returns>
    /// A <see cref="ValidationResult"/> indicating whether the record satisfies the
    /// event schema, along with any error messages.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <c>null</c>.</exception>
    public static ValidationResult ValidateEvent(string json)
        => ValidateText(json, EventSchemaLazy.Value);

    /// <summary>
    /// Validates a single headless event record supplied as a <see cref="JsonElement"/>.
    /// </summary>
    /// <param name="evt">The event record.</param>
    /// <returns>
    /// A <see cref="ValidationResult"/> indicating whether the record satisfies the
    /// event schema, along with any error messages.
    /// </returns>
    public static ValidationResult ValidateEvent(JsonElement evt)
        => ValidateElement(evt, EventSchemaLazy.Value);

    private static ValidationResult ValidateText(string json, JsonSchema schema)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Failure(new[] { $"Invalid JSON: {ex.Message}" });
        }

        using (document)
        {
            return Evaluate(document.RootElement, schema);
        }
    }

    private static ValidationResult ValidateElement(JsonElement element, JsonSchema schema)
        => Evaluate(element, schema);

    private static ValidationResult Evaluate(JsonElement instance, JsonSchema schema)
    {
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        var results = schema.Evaluate(instance, options);
        if (results.IsValid)
        {
            return ValidationResult.Success();
        }

        var errors = CollectErrors(results);
        if (errors.Count == 0)
        {
            errors.Add("Document did not match the schema.");
        }

        return ValidationResult.Failure(errors);
    }

    private static List<string> CollectErrors(EvaluationResults results)
    {
        var messages = new List<string>();
        Walk(results, messages);
        return messages;
    }

    private static void Walk(EvaluationResults node, List<string> messages)
    {
        if (!node.IsValid && node.Errors is { Count: > 0 })
        {
            var location = node.InstanceLocation.ToString();
            location = string.IsNullOrEmpty(location) ? "(root)" : location;
            foreach (var error in node.Errors)
            {
                messages.Add($"{location}: {error.Value}");
            }
        }

        if (node.Details is not null)
        {
            foreach (var child in node.Details)
            {
                Walk(child, messages);
            }
        }
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(HeadlessConfigContract).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema resource '{resourceName}' was not found in assembly '{assembly.FullName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
