namespace Andy.Cli.HeadlessConfig;

// C# model of schemas/headless-config.v1.json (AQ1, rivoli-ai/andy-cli#46).
// JSON ↔ C# naming is handled by a SnakeCaseLower naming policy at the
// serializer level so the schema file stays the single source of truth
// for wire names — no per-property [JsonPropertyName] sprinkled here.
//
// Strict shape checks (required fields, enum closures, oneOf transport
// variants) are enforced by JsonSchema.Net against the embedded schema
// before this type is ever populated; records below can therefore stay
// lenient (nullable optional fields, loose HeadlessTool discriminator).

public sealed record HeadlessRunConfig
{
    public int SchemaVersion { get; init; }
    public Guid RunId { get; init; }
    public HeadlessAgent Agent { get; init; } = new();
    public HeadlessModel Model { get; init; } = new();
    public IReadOnlyList<HeadlessTool> Tools { get; init; } = [];
    public HeadlessWorkspace Workspace { get; init; } = new();
    public IReadOnlyDictionary<string, string>? EnvVars { get; init; }
    public HeadlessOutput Output { get; init; } = new();
    public HeadlessEventSink? EventSink { get; init; }
    public Guid? PolicyId { get; init; }
    public IReadOnlyList<string>? Boundaries { get; init; }
    public HeadlessLimits Limits { get; init; } = new();
}

public sealed record HeadlessAgent
{
    public string Slug { get; init; } = string.Empty;
    public int? Revision { get; init; }
    public string Instructions { get; init; } = string.Empty;
    public string? OutputFormat { get; init; }
}

public sealed record HeadlessModel
{
    public string Provider { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string? ApiKeyRef { get; init; }
}

// Loose discriminated shape matching the schema's oneOf: MCP bindings carry
// Endpoint, CLI bindings carry Binary (+ optional Command). Transport is
// the discriminator consumers key off — schema validation has already
// rejected any binding with the wrong combination of fields.
public sealed record HeadlessTool
{
    public string Name { get; init; } = string.Empty;
    public string Transport { get; init; } = string.Empty;
    public string? Endpoint { get; init; }
    public string? Binary { get; init; }
    public IReadOnlyList<string>? Command { get; init; }
}

public sealed record HeadlessWorkspace
{
    public string Root { get; init; } = string.Empty;
    public string? Branch { get; init; }
}

public sealed record HeadlessOutput
{
    public string File { get; init; } = string.Empty;
    public string Stream { get; init; } = string.Empty;
}

public sealed record HeadlessEventSink
{
    public string? NatsSubject { get; init; }
    public string? Path { get; init; }
}

public sealed record HeadlessLimits
{
    public int MaxIterations { get; init; }
    public int TimeoutSeconds { get; init; }
}
