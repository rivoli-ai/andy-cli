using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Andy.Cli.Headless;

internal sealed record HeadlessEventEnvelope(
    int SchemaVersion,
    DateTimeOffset Ts,
    string Kind,
    JsonObject Data);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HeadlessEventEnvelope))]
internal sealed partial class HeadlessEventJsonContext : JsonSerializerContext;
