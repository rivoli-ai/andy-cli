# Design Brief: MCP Tool Endpoint Resolution -- Gateway Base URL vs Per-Tool Endpoints

> Author: Andy API Designer (contract surface specialist)
> Date: 2026-07-02
> Status: Proposed
> Affects: `headless-config.v1.json` schema, `HeadlessRunConfig`, `HeadlessToolHost`, `HeadlessConfigLoader`
> Supersedes: ADR 0002 (promotes from Proposed to Accepted upon implementation)
> Breaking change: Additive -- no version bump to v2

---

## 1. Problem Statement

Every MCP tool binding in `headless-config.v1.json` currently requires a fully-qualified
`endpoint` URI. The container runtime already injects `ANDY_MCP_URL` (the mcp-gateway
base) as a reserved environment variable, and the schema's own description acknowledges
the convention "usually `$ANDY_MCP_URL/<tool-name>`" -- yet the config producer
(andy-containers / conductor) must re-inject the same base into every tool entry.

With N MCP tools, a single gateway URL change touches N config entries. The resulting
duplication inflates config size, complicates generation, and creates a drift surface:
if the gateway rotates (blue/green deploy, cert renewal, local-dev port shift), stale
per-tool entries silently break.

**Question:** Should the config schema gain an optional top-level `mcp_gateway` field
so MCP tool bindings can omit `endpoint` and have it auto-derived as
`{mcp_gateway}/{tool-name}`?

## 2. Options Evaluated

### Option A: Gateway Base URL

Add an optional `mcp_gateway` string field to the top-level config object. When
present, any MCP tool binding whose `endpoint` is absent gets its endpoint resolved
as `{mcp_gateway}/{tool-name}`. Per-tool `endpoint` overrides the gateway when both
are present.

**Config shape:**

```json
{
  "schema_version": 1,
  "mcp_gateway": "https://mcp.internal/tools",
  "tools": [
    { "name": "fs.patch", "transport": "mcp" },
    { "name": "read_file", "transport": "mcp" },
    { "name": "andy-tasks-cli", "transport": "cli", "binary": "andy-tasks-cli" }
  ]
}
```

### Option B: Per-Tool Endpoints (Status Quo)

Keep the current design: every MCP tool binding must specify its full `endpoint` URI.
No schema change. No resolution logic.

**Config shape:**

```json
{
  "schema_version": 1,
  "tools": [
    { "name": "fs.patch", "transport": "mcp", "endpoint": "https://mcp.internal/tools/fs.patch" },
    { "name": "read_file", "transport": "mcp", "endpoint": "https://mcp.internal/tools/read_file" },
    { "name": "andy-tasks-cli", "transport": "cli", "binary": "andy-tasks-cli" }
  ]
}
```

## 3. Tradeoff Analysis

| Criterion | Option A (Gateway) | Option B (Per-Tool) | Weight |
| --- | --- | --- | --- |
| Config brevity at scale (N MCP tools) | O(1) base + O(N) lightweight (~2 fields each) | O(N) verbose (~3 fields each, repeated base) | High |
| Gateway URL change blast radius | 1 field | N fields | High |
| Runtime complexity added | +25 lines (`ResolveMcpGateway` helper + fallback branch) | None | Low |
| Schema backward compatibility | Additive (optional field, no required-field changes) | N/A | Medium |
| Multi-gateway support | Override per tool via explicit `endpoint` | Native per tool | Low (no current need) |
| Debuggability | Explicit after single substitution | Explicit always | Low |
| `$ANDY_MCP_URL` env var usage | Direct consumer -- the field references it | Indirect -- each tool entry bakes it in | Medium |

### Detailed Assessment

**Config brevity.** With 5 MCP tools (coding-headless sample), Option A reduces the
tool array from ~20 lines of MCP-specific config to ~10 lines plus a single
`mcp_gateway` line. The savings grow linearly with tool count. Conductor config
generators simplify their template logic.

**Blast radius.** Gateway rotation (blue/green, cert renewal, port change) is the
primary operational scenario. Option A makes it a single-field edit. Option B
requires N edits and config regeneration. In CI/CD pipelines this means a
rebuild-and-redeploy cycle for what should be a one-line change.

**Complexity.** The resolution logic is a 25-line static helper
(`ResolveMcpGateway`) plus a 5-line fallback branch in the existing
`case "mcp":` block of `HeadlessToolHost.BuildAsync`. No new types, no new
abstractions, no per-protocol logic duplication. The resolved endpoint is a
concrete URI at connection time -- no hidden indirection.

**Backward compatibility.** The schema change is strictly additive:
- Top-level `mcp_gateway` is optional; absent means "no gateway, use per-tool endpoints."
- MCP tool binding `required` array shrinks from `["name", "transport", "endpoint"]`
  to `["name", "transport"]`; `endpoint` remains as an allowed property.
- Existing configs with per-tool `endpoint` continue to work unchanged.
- The C# `McpGateway` property is `string?` with null default; deserialization is
  unaffected.
- Schema version stays at 1 (no structural breaking change).

**Multi-gateway.** A future scenario where some tools live on gateway A and others on
gateway B is handled by the per-tool `endpoint` override. No dedicated feature is
needed.

**Env var substitution.** The `mcp_gateway` value supports `$ANDY_MCP_URL` substitution
at load time, making the link between the env var and the config explicit rather than
implicit. The container runtime sets `ANDY_MCP_URL`; the config references it.

## 4. Recommendation

**Adopt Option A (Gateway Base URL).**

Rationale:
1. The conductor platform already computes `ANDY_MCP_URL` and injects it. Today the
   config producer re-injects the same value into every tool entry -- pure duplication.
2. Gateway rotation is a real operational scenario (blue/green deploys, cert renewal,
   local dev port shifts). Making it a one-field change is a meaningful operational
   improvement.
3. The complexity cost is minimal: one optional schema field, one nullable C# property,
   one 25-line resolution helper.
4. Full backward compatibility: no existing config breaks.
5. The per-tool `endpoint` override preserves flexibility for multi-gateway or
   local-dev scenarios without any additional mechanism.

## 5. Exact Contract Changes

### 5.1 Config Schema (`schemas/headless-config.v1.json`)

**Add** `mcp_gateway` at the top-level `properties` object (after `schema_version`):

```json
"mcp_gateway": {
  "type": "string",
  "format": "uri",
  "description": "Base URL for MCP tool endpoints. When present, MCP tool bindings without an explicit 'endpoint' have their endpoint resolved as {mcp_gateway}/{tool-name}. Supports $ANDY_MCP_URL substitution. Per-tool 'endpoint' overrides this when both are present."
}
```

No change to top-level `required` array (field remains optional).

**Modify** the MCP tool binding variant (`tools[].items.oneOf[0]`):

Change `required` from:
```json
"required": ["name", "transport", "endpoint"]
```
to:
```json
"required": ["name", "transport"]
```

The `endpoint` property definition remains unchanged (still accepts a URI string).

### 5.2 C# DTO (`src/Andy.Cli/HeadlessConfig/HeadlessRunConfig.cs`)

Add to `HeadlessRunConfig` (after `RunId`, before `Agent`):

```csharp
/// <summary>
/// Optional base URL for MCP tool endpoints. When present, MCP tool bindings
/// without an explicit Endpoint have their endpoint resolved as
/// {McpGateway}/{tool-name}. Supports $ANDY_MCP_URL substitution from the
/// process environment. Per-tool Endpoint overrides this when both are present.
/// </summary>
public string? McpGateway { get; init; }
```

### 5.3 Resolution Logic (`src/Andy.Cli/Headless/HeadlessToolHost.cs`)

In `BuildAsync`, before the tool loop, resolve the gateway once:

```csharp
string? resolvedGateway = ResolveMcpGateway(config);
```

Add new private static helper:

```csharp
private static string? ResolveMcpGateway(HeadlessRunConfig config)
{
    if (string.IsNullOrEmpty(config.McpGateway))
        return null;

    // Substitute the reserved env var if the gateway value references it.
    return config.McpGateway
        .Replace("$ANDY_MCP_URL",
            Environment.GetEnvironmentVariable("ANDY_MCP_URL") ?? string.Empty,
            StringComparison.Ordinal);
}
```

In the `case "mcp":` branch of the tool loop, modify the endpoint resolution
to use gateway fallback. Replace the existing null-check guard:

```csharp
case "mcp":
{
    if (string.IsNullOrEmpty(tool.Endpoint) && resolvedGateway is null)
    {
        throw new InvalidOperationException(
            $"MCP tool '{tool.Name}' has no endpoint and no mcp_gateway is configured.");
    }

    var endpoint = !string.IsNullOrEmpty(tool.Endpoint)
        ? tool.Endpoint
        : $"{resolvedGateway!.TrimEnd('/')}/{tool.Name}";

    // ... rest of existing MCP wiring, using `endpoint` local variable
    // instead of `tool.Endpoint` throughout ...
}
```

**Important:** The existing deduplication dictionary (`mcpSessionsByEndpoint`) must
key on the resolved `endpoint` local variable, not `tool.Endpoint`, so that
gateway-derived endpoints and explicit endpoints coexist correctly.

### 5.4 Config Loader Validation (`src/Andy.Cli/HeadlessConfig/HeadlessConfigLoader.cs`)

The schema change makes `endpoint` optional for MCP bindings. The schema alone cannot
enforce the invariant "every MCP tool has either an endpoint or a top-level gateway."
Add a semantic validation pass after deserialization in `TryLoadAsync`, before returning
the result:

```csharp
// Semantic check: MCP tools without an endpoint require a top-level mcp_gateway.
if (string.IsNullOrEmpty(config.McpGateway))
{
    var mcpWithoutEndpoint = config.Tools
        .Where(t => t.Transport == "mcp" && string.IsNullOrEmpty(t.Endpoint))
        .Select(t => t.Name)
        .ToList();

    if (mcpWithoutEndpoint.Count > 0)
    {
        return HeadlessConfigLoadResult.Fail(
            $"MCP tools [{string.Join(", ", mcpWithoutEndpoint)}] have no endpoint " +
            "and no mcp_gateway is configured.");
    }
}
```

### 5.5 Documentation Updates

| File | Change |
| --- | --- |
| `docs/headless-runtime.md` | Add `mcp_gateway` to the Top-level fields table (No, string/uri). Update MCP tool binding section: `endpoint` is now optional; when absent, resolved from `mcp_gateway`. Update Reserved environment variables table to note `mcp_gateway` as the primary consumer of `$ANDY_MCP_URL`. |
| `docs/adr/0002-mcp-gateway-base-url.md` | Change status from `Proposed` to `Accepted`. |
| `schemas/samples/coding-headless.json` | Add `"mcp_gateway": "https://mcp.internal/tools"` and convert the two MCP tools to gateway form (remove explicit `endpoint`). |
| `schemas/samples/planning-headless.json` | Add `"mcp_gateway": "https://mcp.internal/tools"` and convert the two MCP tools to gateway form. |
| `schemas/samples/triage-headless.json` | Add `"mcp_gateway": "https://mcp.internal/tools"` and convert the one MCP tool to gateway form. |

### 5.6 Sample Config Update (coding-headless.json)

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
"mcp_gateway": "https://mcp.internal/tools",
"tools": [
    { "name": "fs.patch", "transport": "mcp" },
    { "name": "container.exec", "transport": "mcp" },
    ...
]
```

## 6. Tests to Add/Update

| # | Test | Location | Assertion |
| --- | --- | --- | --- |
| 1 | `McpGateway_resolution_substitutes_env_var` | `HeadlessToolHostTests` (new file or extend existing) | Set `McpGateway` to `$ANDY_MCP_URL`, set `ANDY_MCP_URL` env var to `http://localhost:9090/tools`, assert resolved endpoint is `http://localhost:9090/tools/my.tool`. |
| 2 | `McpGateway_per_tool_endpoint_overrides` | `HeadlessToolHostTests` | Set `McpGateway` to `http://gateway/tools`, give a tool explicit `endpoint: http://other/endpoint`, assert the explicit endpoint is used. |
| 3 | `McpGateway_missing_both_throws` | `HeadlessToolHostTests` | MCP tool with no `endpoint` and no `McpGateway` throws `InvalidOperationException`. |
| 4 | `McpGateway_null_is_noop` | `HeadlessToolHostTests` | When `McpGateway` is null and all MCP tools have `endpoint`, behavior is identical to today. |
| 5 | `ConfigLoader_rejects_mcp_tools_without_endpoint_or_gateway` | `HeadlessConfigSchemaTests` or `HeadlessConfigLoaderTests` | Semantic validation catches the unsupported combination and returns `ConfigError`. |
| 6 | `Schema_validates_mcp_tool_without_endpoint` | `HeadlessConfigSchemaTests` | An MCP binding with `name` + `transport` but no `endpoint` passes schema validation (the schema allows it). |
| 7 | `Schema_validates_mcp_tool_with_endpoint` | `HeadlessConfigSchemaTests` | Existing MCP bindings with explicit `endpoint` still pass (no regression). |
| 8 | Update `FixtureValidatesAgainstSchema` | `HeadlessConfigSchemaTests` | All three sample fixtures pass after the schema change. |

## 7. Implementation Steps (for coding agent)

1. **Schema** -- Edit `schemas/headless-config.v1.json`: add `mcp_gateway` property, remove `endpoint` from MCP binding `required` array.
2. **C# DTO** -- Add `McpGateway` property to `HeadlessRunConfig` in `src/Andy.Cli/HeadlessConfig/HeadlessRunConfig.cs`.
3. **Resolution logic** -- Add `ResolveMcpGateway` helper and modify the `case "mcp":` fallback branch in `src/Andy.Cli/Headless/HeadlessToolHost.cs`.
4. **Semantic validation** -- Add post-deserialization check in `src/Andy.Cli/HeadlessConfig/HeadlessConfigLoader.cs`.
5. **Sample configs** -- Update all three files under `schemas/samples/` to use `mcp_gateway`.
6. **Documentation** -- Update `docs/headless-runtime.md` and promote ADR 0002 to Accepted.
7. **Tests** -- Add the eight test cases listed in section 6.
8. **Validate** -- `dotnet build` (zero warnings, TreatWarningsAsErrors) and `dotnet test` (all green).
9. **Format** -- `dotnet format` clean (no formatting drift).

## 8. Out of Scope

- **Multiple gateway support.** Not needed today. Per-tool `endpoint` override handles
  the case without any dedicated feature.
- **`$ANDY_MCP_URL` as a first-class env var reference in the schema.** The substitution
  is a runtime behavior, not a schema-level construct. The schema describes the shape;
  the loader performs the substitution.
- **Changing the interactive CLI tool surface.** Headless config only.
- **Modifying `McpRemoteTool`.** The adapter receives a resolved endpoint; it does not
  need to know about the gateway concept.
- **Schema version bump.** The change is additive (new optional field, relaxed
  requirement). Schema version stays at 1.

## 9. Breaking Change Assessment

**Additive. No breaking change.**

- No existing field is removed or renamed.
- No required field changes at the top level.
- MCP binding `required` shrinks (from `[name, transport, endpoint]` to
  `[name, transport]`), which is a relaxation, not a tightening -- all
  previously valid configs remain valid.
- The C# `McpGateway` property is nullable with a null default.
- Schema version remains 1.

## 10. Decision Required

- [ ] Approve the schema change as additive (no version bump to v2).
- [ ] Approve `mcp_gateway` as the canonical field name (matches existing
      `ANDY_MCP_URL` convention and ADR 0002).
- [ ] Approve `$ANDY_MCP_URL` string substitution in `mcp_gateway` value.
- [ ] Approve per-tool `endpoint` override semantics (when both are present,
      `endpoint` wins).
