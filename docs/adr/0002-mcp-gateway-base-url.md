# ADR 0002: MCP Tool Endpoint Resolution -- Gateway Base URL vs Per-Tool Endpoints

> Status: Accepted
> Date: 2026-07-02
> Deciders: API Designer (contract surface), Conductor platform team
> Relates to: Epic AQ (rivoli-ai/andy-cli#44), Epic Y5 (mcp-gateway), headless-config.v1 schema

## Context

The headless runtime (`andy-cli run --headless`) resolves MCP tool endpoints from the
`tools[]` array in `headless-config.v1.json`. Every MCP binding currently requires a
fully-qualified `endpoint` URI:

```json
{
  "name": "fs.patch",
  "transport": "mcp",
  "endpoint": "https://mcp.internal/tools/fs.patch"
}
```

The container runtime already injects `ANDY_MCP_URL` (the mcp-gateway base) as a
reserved environment variable. The docs describe the convention as "usually
`$ANDY_MCP_URL/<tool-name>`" but leave the actual string concatenation to the config
producer (andy-containers / conductor). This creates duplication and a coupling
surface: every tool entry bakes the gateway base into its endpoint, so a gateway
URL change forces a config regeneration.

### Goal

Evaluate whether to add a `mcp_gateway` field to the headless config so that MCP
tool bindings can omit `endpoint` and have it auto-derived, while remaining
backward-compatible with the per-tool `endpoint` form.

## Option A: Gateway Base URL

Add an optional `mcp_gateway` string field to the top-level config object. When
present, any MCP tool binding whose `endpoint` is absent gets its endpoint
resolved as `{mcp_gateway}/{tool-name}`.

### Config shape (schema diff)

```json
{
  "schema_version": 1,
  "mcp_gateway": "$ANDY_MCP_URL",
  "tools": [
    { "name": "fs.patch", "transport": "mcp" },
    { "name": "read_file", "transport": "mcp" },
    { "name": "andy-tasks-cli", "transport": "cli", "binary": "andy-tasks-cli" }
  ]
}
```

### Schema change (`headless-config.v1.json`)

- Add `mcp_gateway` as an optional string property (format: uri) at the top level.
- For the `MCP tool binding` variant in the `tools[].items.oneOf[0]`:
  - Make `endpoint` optional (remove from `required`). Keep it as an allowed property.
  - The invariant becomes: at least one of `mcp_gateway` (top-level) or `endpoint`
    (per-tool) must be present for any MCP binding; the loader rejects a config where
    an MCP tool has no `endpoint` and no top-level `mcp_gateway`.

### C# model change (`HeadlessRunConfig.cs`)

```csharp
public sealed record HeadlessRunConfig
{
    // ... existing fields ...
    public string? McpGateway { get; init; }   // NEW: base URL for MCP tools
    // ... rest unchanged ...
}
```

### Resolution logic (`HeadlessToolHost.BuildAsync`)

```csharp
// After schema validation, before the tool loop:
string? resolvedGateway = null;
if (!string.IsNullOrEmpty(config.McpGateway))
{
    resolvedGateway = config.McpGateway
        .Replace("$ANDY_MCP_URL", Environment.GetEnvironmentVariable("ANDY_MCP_URL") ?? "");
}

foreach (var tool in tools)
{
    switch (tool.Transport)
    {
        case "mcp":
            var endpoint = tool.Endpoint;
            if (string.IsNullOrEmpty(endpoint))
            {
                if (resolvedGateway is null)
                    throw new InvalidOperationException(
                        $"MCP tool '{tool.Name}' has no endpoint and no mcp_gateway is configured.");
                endpoint = $"{resolvedGateway.TrimEnd('/')}/{tool.Name}";
            }
            // ... rest of MCP wiring unchanged ...
    }
}
```

### Benefits

| Dimension | Assessment |
| --- | --- |
| Config brevity | Config drops from ~5 lines per MCP tool to ~2 lines (name + transport). |
| Decoupling | Gateway URL change is a single-field edit, not N tool edits. |
| Environment variable resolution | `$ANDY_MCP_URL` substitution in `mcp_gateway` is natural and explicit. |
| Backward compat | Fully backward-compatible: existing configs with per-tool `endpoint` continue to work unchanged. Per-tool `endpoint` overrides the gateway when both are present. |
| Complexity | ~20 lines of resolution logic in `HeadlessToolHost`. One new optional field in the schema and C# model. |
| Debuggability | The resolved endpoint is still a concrete URI at connection time; no hidden indirection beyond the single substitution. |

### Risks

- **Mixed gateway + explicit endpoint**: A tool with both `mcp_gateway` (top-level) and
  `endpoint` (per-tool) could be confusing. Mitigation: per-tool `endpoint` wins; document this.
- **Multiple gateways**: A future need for tools on different gateways is not served by a
  single `mcp_gateway`. Mitigation: per-tool `endpoint` override handles this; no feature
  needed.

## Option B: Per-Tool Endpoints (Status Quo)

Keep the current design: every MCP tool binding must specify its full `endpoint` URI.

### Benefits

| Dimension | Assessment |
| --- | --- |
| Simplicity | No new field, no resolution logic. The endpoint is always explicit in the config. |
| Flexibility | Each tool can point to a different gateway, port, or host without any override mechanism. |
| No schema change | Zero schema version impact; no migration path needed. |

### Drawbacks

| Dimension | Assessment |
| --- | --- |
| Config verbosity | Each MCP tool is 3 required fields (name, transport, endpoint). Scales linearly with tool count. |
| Duplication | The gateway base is repeated N times. A gateway URL change touches every MCP tool entry. |
| Tight coupling | The config producer (conductor/andy-containers) must inject the full URL per tool, even though `ANDY_MCP_URL` is already in the environment. |
| Drift risk | If the gateway URL rotates (blue/green deploy, cert renewal), stale per-tool entries silently break. |

## Tradeoff Summary

| Criterion | Option A (Gateway) | Option B (Per-Tool) | Weight |
| --- | --- | --- | --- |
| Config brevity at scale (N tools) | O(1) + O(N) light | O(N) heavy | High |
| Gateway URL change blast radius | 1 field | N fields | High |
| Runtime complexity | +20 lines | None | Low |
| Schema backward compat | Additive (optional) | N/A | Medium |
| Multi-gateway support | Override per tool | Native | Low (current) |
| Debuggability | Explicit after resolution | Explicit always | Low |

## Recommendation

**Adopt Option A (Gateway Base URL)** as an additive, backward-compatible extension
to `headless-config.v1` (schema version remains 1 -- the change is purely additive
and does not alter any required field or invariant).

Rationale: the conductor platform already computes `ANDY_MCP_URL` and injects it
into the container. Today the config producer must re-inject the same value into
every tool entry. A single `mcp_gateway` field eliminates that duplication, reduces
config generation complexity, and makes gateway rotation a one-field change. The
per-tool `endpoint` override is preserved for mixed-gateway or local-dev scenarios.

## Exact Contract Change

### 1. Schema (`schemas/headless-config.v1.json`)

Add at the top-level `properties` object:

```json
"mcp_gateway": {
  "type": "string",
  "format": "uri",
  "description": "Base URL for MCP tool endpoints. When present, MCP tool bindings without an explicit 'endpoint' have their endpoint resolved as {mcp_gateway}/{tool-name}. Supports $ANDY_MCP_URL substitution. Per-tool 'endpoint' overrides this when both are present."
}
```

No changes to `required` at the top level (field remains optional).

In the `MCP tool binding` variant (`tools[].items.oneOf[0]`):

Change `required` from `["name", "transport", "endpoint"]` to `["name", "transport"]`.
The `endpoint` property definition remains unchanged (still accepts a URI string).

### 2. C# DTO (`src/Andy.Cli/HeadlessConfig/HeadlessRunConfig.cs`)

Add to `HeadlessRunConfig`:

```csharp
/// <summary>
/// Optional base URL for MCP tool endpoints. When present, MCP tool bindings
/// without an explicit Endpoint have their endpoint resolved as
/// {McpGateway}/{tool-name}. Supports $ANDY_MCP_URL substitution from the
/// process environment. Per-tool Endpoint overrides this.
/// </summary>
public string? McpGateway { get; init; }
```

### 3. Resolution logic (`src/Andy.Cli/Headless/HeadlessToolHost.cs`)

In `BuildAsync`, before the tool loop, resolve the gateway:

```csharp
string? resolvedGateway = ResolveMcpGateway(config);
```

New private helper:

```csharp
private static string? ResolveMcpGateway(HeadlessRunConfig config)
{
    if (string.IsNullOrEmpty(config.McpGateway))
        return null;

    // Substitute the reserved env var if the gateway value references it.
    return config.McpGateway
        .Replace("$ANDY_MCP_URL",
            Environment.GetEnvironmentVariable("ANDY_MCP_URL") ?? "",
            StringComparison.Ordinal);
}
```

In the `case "mcp":` branch, after the existing null-check for `tool.Endpoint`,
add a fallback:

```csharp
case "mcp":
{
    var endpoint = tool.Endpoint;
    if (string.IsNullOrEmpty(endpoint))
    {
        if (resolvedGateway is null)
        {
            throw new InvalidOperationException(
                $"MCP tool '{tool.Name}' has no endpoint and no mcp_gateway is configured.");
        }
        endpoint = $"{resolvedGateway.TrimEnd('/')}/{tool.Name}";
    }
    // ... existing connection logic, using `endpoint` instead of `tool.Endpoint` ...
}
```

### 4. Config loader validation (`src/Andy.Cli/HeadlessConfig/HeadlessConfigLoader.cs`)

After schema validation succeeds, add a semantic check:

```csharp
// Validate: every MCP tool either has an endpoint or the config provides a gateway.
if (!string.IsNullOrEmpty(config.McpGateway) ||
    config.Tools.All(t => t.Transport != "mcp" || !string.IsNullOrEmpty(t.Endpoint)))
    return (config, null);

// Some MCP tools lack endpoints and there is no gateway -- the schema allowed
// this (endpoint is optional), but the runtime cannot resolve it.
var mcpWithoutEndpoint = config.Tools
    .Where(t => t.Transport == "mcp" && string.IsNullOrEmpty(t.Endpoint))
    .Select(t => t.Name);
return (null, $"MCP tools [{string.Join(", ", mcpWithoutEndpoint)}] have no endpoint and no mcp_gateway is configured.");
```

### 5. Documentation updates

| File | Change |
| --- | --- |
| `docs/headless-runtime.md` | Add `mcp_gateway` to the Top-level fields table. Update the MCP tool binding section to note the endpoint-gateway fallback. Update the Reserved environment variables section to reference `mcp_gateway` as the primary consumer of `$ANDY_MCP_URL`. |
| `docs/tool-execution-architecture.md` | No change needed (describes the internal execution flow, not the config schema). |
| `schemas/samples/coding-headless.json` | Add `"mcp_gateway": "$ANDY_MCP_URL"` and convert the two MCP tools to use the gateway form (remove explicit `endpoint`). Keep one sample with per-tool endpoint as a reference. |
| `docs/headless-runtime.md` worked example | Update to show both forms (gateway-based and per-tool endpoint). |

### 6. Sample config update (`schemas/samples/coding-headless.json`)

Before:

```json
"tools": [
  { "name": "fs.patch", "transport": "mcp", "endpoint": "https://mcp.internal/tools/fs.patch" },
  { "name": "container.exec", "transport": "mcp", "endpoint": "https://mcp.internal/tools/container.exec" },
  ...
]
```

After:

```json
"mcp_gateway": "$ANDY_MCP_URL",
"tools": [
  { "name": "fs.patch", "transport": "mcp" },
  { "name": "container.exec", "transport": "mcp" },
  ...
]
```

### 7. Tests to add/update

| Test | Location | Assertion |
| --- | --- | --- |
| `McpGateway_resolution_substitutes_env_var` | HeadlessToolHostTests | `mcp_gateway: "$ANDY_MCP_URL"` resolves correctly when `ANDY_MCP_URL` is set. |
| `McpGateway_per_tool_endpoint_overrides` | HeadlessToolHostTests | A tool with explicit `endpoint` ignores `mcp_gateway`. |
| `McpGateway_missing_both_throws` | HeadlessToolHostTests | An MCP tool with no `endpoint` and no `mcp_gateway` throws `InvalidOperationException`. |
| `McpGateway_null_is_noop` | HeadlessToolHostTests | When `mcp_gateway` is null/absent, behavior is unchanged from today. |
| `ConfigLoader_rejects_mcp_tools_without_endpoint_or_gateway` | HeadlessConfigLoaderTests | Semantic validation catches the unsupported combination. |

## Breaking Change Assessment

**Additive.** No existing field is removed or renamed. No required field changes.
Configs that omit `mcp_gateway` and provide per-tool `endpoint` continue to work
identically. The `McpGateway` C# property is nullable with a null default, so
existing deserialization is unaffected. The schema remains version 1.

## Implementation Steps (for coding agent)

1. Update `schemas/headless-config.v1.json` -- add `mcp_gateway`, make `endpoint` optional in MCP variant.
2. Add `McpGateway` to `HeadlessRunConfig.cs`.
3. Add `ResolveMcpGateway` helper and endpoint fallback in `HeadlessToolHost.cs`.
4. Add semantic validation in `HeadlessConfigLoader.cs`.
5. Update `docs/headless-runtime.md` with the new field and fallback behavior.
6. Update `schemas/samples/coding-headless.json` to use `mcp_gateway`.
7. Write unit tests for the five cases above.
8. Run `dotnet build` and `dotnet test` -- zero warnings, all green.
