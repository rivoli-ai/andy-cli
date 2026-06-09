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

    // AX.4 (rivoli-ai/conductor#2091): per-run permission allow-list. Headless is
    // fail-closed (AddAndyCliPermissions(services, null) → no interactive broker),
    // so mutating built-ins (write_file/delete_file/move_file/copy_file/file_editor/
    // replace_text/create_directory) and execute_command resolve to "Ask" → DENY.
    // This field relaxes EXACTLY the listed tools to "Allow" (injected at the
    // PermissionLayer.Injected layer, which overrides the Builtin Ask defaults).
    // Every tool NOT listed stays fail-closed/denied. When ABSENT/null, behavior
    // is unchanged (current fail-closed defaults) — backward compatible.
    //
    // Out of scope here (AX.9 / andy-containers): deriving this list from policies
    // and writing it into the config. AX.4 only CONSUMES it.
    public HeadlessPermissions? Permissions { get; init; }

    public HeadlessLimits Limits { get; init; } = new();
}

public sealed record HeadlessPermissions
{
    // Tool ids the run is permitted to execute (e.g. "write_file", "execute_command").
    // A tool that is normally "Ask" (e.g. write_file) becomes executable when listed;
    // a tool NOT listed stays denied. An empty list grants nothing beyond the
    // auto-allowed read-only built-ins — same as omitting the block entirely.
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
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
