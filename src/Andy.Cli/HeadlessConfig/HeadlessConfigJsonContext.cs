using System.Text.Json.Serialization;

namespace Andy.Cli.HeadlessConfig;

// Source-generated metadata keeps the headless configuration graph available
// when the CLI is published with trimming enabled. Reflection-based metadata
// works in normal builds but is removed from the self-contained release binary.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(HeadlessRunConfig))]
internal sealed partial class HeadlessConfigJsonContext : JsonSerializerContext;
