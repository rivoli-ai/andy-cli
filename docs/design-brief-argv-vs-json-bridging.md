# Design Brief: argv vs JSON Bridging for Headless CLI Subprocess Tools

> Author: Andy API Designer
> Date: 2026-07-15
> Status: **Approved for Implementation**
> Affects: `CliSubprocessTool`, `HeadlessTool` config schema, `headless-config.v1.json`
> Does NOT affect: `McpRemoteTool` (already JSON-native), interactive tool surface

---

## 1. Problem Statement

The headless agent runtime exposes two transport adapters for external tool
bindings: `McpRemoteTool` (JSON-native, passes an `arguments` object verbatim to
the MCP protocol) and `CliSubprocessTool` (argv-only, passes a flat `args`
string array appended to a command prefix).

Today the CLI transport bridges every LLM parameter into positional string
arguments. This creates a structural impedance mismatch when the downstream
CLI binary expects typed, named, or nested input -- the LLM must serialize
structured intent into a positional string array, and the binary must re-parse
it. The question this brief evaluates: should `CliSubprocessTool` gain a JSON
bridging mode alongside (or replacing) the current argv mode?

## 2. Current State

### 2.1 `CliSubprocessTool` argument flow

```
LLM emits tool call:
  { "name": "andy-tasks-cli", "arguments": { "args": ["--json", "search", "my query"] } }

CliSubprocessTool.ExecuteAsync:
  1. Reads parameters["args"]  ->  IEnumerable of strings
  2. Prepends _config.Command (e.g. ["andy-tasks-cli"])
  3. Builds ProcessStartInfo.ArgumentList from the full argv
  4. Spawns process, captures stdout/stderr
  5. Returns ToolResult.Success(stdoutText)
```

The `args` parameter is declared in `ToolMetadata` as:

```csharp
new ToolParameter
{
    Name = "args",
    Description = "Arguments to append to the configured command prefix ...",
    Type = "array",       // items: string
    Required = false,
}
```

### 2.2 `McpRemoteTool` argument flow (for comparison)

```
LLM emits tool call:
  { "name": "read_file", "arguments": { "arguments": { "file_path": "/x", "max_size_mb": 10 } } }

McpRemoteTool.ExecuteAsync:
  1. Reads parameters["arguments"]  ->  raw object (JSON pass-through)
  2. Calls _client.CallToolAsync(remoteTool.Name, rawArgs, ct)
  3. Protocol transmits the object as the MCP "arguments" field
  4. Returns ToolResult from CallToolResult.Content
```

MCP tools already receive structured, named, typed input. CLI tools do not.

### 2.3 Config schema (`headless-config.v1.json`, CLI variant)

```json
{
    "name": "andy-tasks-cli",
    "transport": "cli",
    "binary": "andy-tasks-cli",
    "command": ["--json"]
}
```

No field controls the bridging mode. The schema is `additionalProperties: false`.

## 3. Evaluation

### 3.1 Pure argv (status quo)

| Dimension | Assessment |
| --- | --- |
| **Structured data** | None. The LLM must flatten typed intent into positional strings. For tools that accept only flags and positional args (git, ls, grep), this is natural. For tools with named options, nested config, or typed parameters, the LLM must invent a string encoding and the CLI must reverse it. |
| **Backward compatibility** | Full. No config change, no schema change, no behavioral change for existing CLI bindings. |
| **Tooling complexity** | Minimal. `CliSubprocessTool` is ~200 lines today. Adding JSON bridging would roughly double the code surface and require a new config field. |
| **Security** | Strong. `ProcessStartInfo.ArgumentList` prevents shell injection; every arg is a separate argv entry. No parsing, no expansion. |
| **LLM usability** | Adequate for simple CLIs. Degrades when the LLM must construct complex command lines (e.g. `--config '{"key": "value"}'` -- quoting hell across shells). |

### 3.2 JSON bridging (via stdin)

| Dimension | Assessment |
| --- | --- |
| **Structured data** | Full. Named parameters, typed values, nested objects -- the LLM emits what it naturally produces and the CLI parses it directly. |
| **Backward compatibility** | Requires a new config field (`input_mode` or similar) to opt in; default must remain `argv` so existing configs are unaffected. |
| **Tooling complexity** | Moderate. `CliSubprocessTool` must: (a) serialize parameters to JSON, (b) write to the child process stdin, (c) close stdin so the process knows input is complete, (d) optionally declare a JSON schema in ToolMetadata so the LLM receives typed parameter hints. ~100-150 lines of new logic. |
| **Security** | Comparable. The JSON is serialized by .NET (no shell involvement). The child process reads a bounded payload from a pipe. Risk shifts to the child process needing a JSON parser, but that is the consuming tool's responsibility. |
| **LLM usability** | Superior for structured tools. The LLM emits `{"query": "my search", "limit": 10}` instead of `["--query", "my search", "--limit", "10"]`. Fewer token-wasting string-encoding round-trips. |

### 3.3 JSON bridging (via argv blob)

A middle ground: pass a single `--json '{"key":"value"}' ` argv entry.

| Dimension | Assessment |
| --- | --- |
| **Structured data** | Full on the data side. |
| **Backward compatibility** | Requires config opt-in, same as stdin. |
| **Tooling complexity** | Slightly simpler than stdin (no pipe management), but the child process must still parse a JSON string from its argv -- unusual for CLI tools. Quoting/escaping can interact with platform-specific argv limits. |
| **Security** | Weaker than stdin. The JSON blob passes through `ArgumentList`, which is safe from shell injection but subject to OS argv length limits (~128 KB on Linux, ~32 KB on Windows). Large payloads (file content, bulk data) may silently truncate. |
| **LLM usability** | Same as stdin for structured data, but the LLM must produce a single string rather than a structured object, which is a regression. |

## 4. Recommendation

**Adopt stdin-based JSON bridging as an opt-in mode, retaining argv as the default.**

Rationale:

1. **Backward compatible.** Zero existing configs break. The new `input_mode` field
   defaults to `"argv"` when absent. The schema change is additive (new optional
   property inside the CLI variant).

2. **Structured data where it matters.** CLI tools that wrap complex services
   (task managers, issue trackers, code analyzers) benefit from named, typed
   parameters. Simple utilities (echo, ls, git) keep using argv.

3. **Security parity.** Stdin piping avoids argv length limits and keeps the
   serialization entirely within .NET's control.

4. **Complexity is bounded.** The implementation is a new code path inside
   `CliSubprocessTool.ExecuteAsync` gated by a config flag. `BuildMetadata`
   switches its parameter declaration based on the mode. Total added surface:
   ~120 lines of production code plus tests.

5. **Consistency with MCP.** Both transports can offer structured input to the
   LLM, reducing the cognitive split between "MCP tools get JSON, CLI tools
   get strings" that complicates agent instructions.

### 4.1 What this does NOT change

- The MCP transport (`McpRemoteTool`) is unaffected.
- The interactive tool surface (built-in Andy.Tools tools) is unaffected.
- The `ParameterMapper` alias/normalization layer is unaffected; it runs before
  the transport adapter and operates on the `Dictionary<string, object?>` that
  the LLM produces. JSON-mode CLI tools receive the same mapped dictionary.
- The config schema's `additionalProperties: false` constraint is preserved by
  adding the new field to the existing CLI variant definition.

## 5. Contract Changes

### 5.1 Config schema (`schemas/headless-config.v1.json`)

Add `input_mode` to the CLI tool binding variant (the second branch of the
`tools[].items.oneOf` array). The change is additive: new optional property
inside the existing CLI variant definition, which carries `additionalProperties:
false`.

**Exact diff** -- add `"input_mode"` to the `properties` object of the CLI
variant (lines ~119-131 of `schemas/headless-config.v1.json`):

```json
"input_mode": {
    "type": "string",
    "enum": ["argv", "json"],
    "default": "argv",
    "description": "How the agent's parameters reach the subprocess. 'argv' (default): parameters are flattened into a string array appended to command[]. 'json': parameters are serialized as a JSON object and written to the subprocess's stdin, then stdin is closed. The subprocess reads the JSON from stdin and the exit/error semantics are unchanged."
}
```

**Backward compatibility note:** Existing configs omit `input_mode` and get
`"argv"` (the current behavior). The schema's `default` keyword signals this
to validators; the runtime treats `null`/absent as `"argv"`.

### 5.2 C# model (`src/Andy.Cli/HeadlessConfig/HeadlessRunConfig.cs`)

Add one nullable property to `HeadlessTool`:

```csharp
public sealed record HeadlessTool
{
    public string Name { get; init; } = string.Empty;
    public string Transport { get; init; } = string.Empty;
    public string? Endpoint { get; init; }
    public string? Binary { get; init; }
    public IReadOnlyList<string>? Command { get; init; }

    // NEW: Bridging mode for CLI transport. Null/absent = "argv".
    public string? InputMode { get; init; }
}
```

The JSON property name `input_mode` is already handled by the
`SnakeCaseLower` naming policy configured in `HeadlessConfigLoader.s_jsonOptions`.

### 5.3 `CliSubprocessTool` behavioral change (`src/Andy.Cli/Headless/Tools/CliSubprocessTool.cs`)

The `ExecuteAsync` method gains a branch:

```
if (_config.InputMode is "json")
    -> Serialize parameters (excluding reserved keys) as JSON
    -> Pipe to stdin via process.StandardInput
    -> Close stdin
    -> Read stdout/stderr as before
else (default: null or "argv")
    -> Current behavior unchanged (MaterializeArgv)
```

The `BuildMetadata` method switches its parameter declaration based on mode:

```csharp
// argv mode (current, and default):
Parameters = [new ToolParameter { Name = "args", Type = "array", ... }]

// json mode:
Parameters = [new ToolParameter { Name = "arguments", Type = "object", ... }]
```

The JSON mode reuses the same parameter name (`arguments`) as `McpRemoteTool`,
giving the LLM a consistent mental model across transports.

**`ValidateParameters` update:** In JSON mode, validate `arguments` as a
non-null object (not a string, not an array). In argv mode, the existing
`args` validation is unchanged.

**Stdin protocol details:**
1. Serialize the `arguments` dictionary (the value of `parameters["arguments"]`)
   as JSON using `System.Text.Json.JsonSerializer.SerializeToUtf8Bytes` with
   `JsonSerializerOptions { PropertyNamingPolicy = null }` (preserve the
   LLM's property names verbatim -- do NOT apply SnakeCaseLower).
2. Write the byte array to `process.StandardInput.BaseStream`.
3. Call `process.StandardInput.Close()` (sends EOF).
4. Read stdout and stderr as today.
5. Exit code semantics are unchanged (0 = success, non-zero = failure).

**Max payload guard:** The JSON payload is bounded by the `ToolOutputLimits`
constants or a new configurable ceiling (default 1 MB). If the serialized
payload exceeds this, return `ToolResult.Failure` with a clear message rather
than writing to the pipe.

### 5.4 Documentation updates

| File | Change |
| --- | --- |
| `docs/headless-runtime.md` | Add `input_mode` field to the `cli` transport table (Section "Tool transports" > `cli`). Add a subsection "JSON input mode" explaining the stdin protocol. Update the worked example to show both modes. |
| `docs/tool-execution-architecture.md` | Add a note in the Headless Mode section (line ~265): "CLI tools can opt into JSON bridging via `input_mode: json`, which writes the LLM's parameters as a JSON object to the subprocess's stdin." |
| `schemas/samples/` | Add one sample config `json-stdio-headless.json` using `input_mode: json`. |

### 5.5 Example config (JSON mode)

```json
{
    "schema_version": 1,
    "run_id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "agent": {
        "slug": "issue-triage",
        "instructions": "You are an issue triage agent. Read open issues, categorize them, and write the triage report."
    },
    "model": { "provider": "openrouter", "id": "xiaomi/mimo-v2.5" },
    "tools": [
        {
            "name": "andy-issues-cli",
            "transport": "cli",
            "binary": "andy-issues-cli",
            "command": ["andy-issues-cli"],
            "input_mode": "json"
        }
    ],
    "workspace": { "root": "/workspace" },
    "output": { "file": "/workspace/.andy/output.txt", "stream": "stdout" },
    "limits": { "max_iterations": 40, "timeout_seconds": 1800 }
}
```

With this config, when the LLM calls `andy-issues-cli` with
`{"arguments": {"query": "open bugs", "limit": 10}}`, the runtime:

1. Serializes `{"query": "open bugs", "limit": 10}` as JSON (UTF-8, no BOM).
2. Writes the bytes to the subprocess's stdin.
3. Closes stdin (EOF signal).
4. The subprocess reads JSON from stdin, processes it, writes result to stdout.
5. Runtime captures stdout, returns as `ToolResult.Success`.

## 6. Implementation Scope

| Work item | Owner | Files | Risk |
| --- | --- | --- | --- |
| Schema change (additive) | API Designer | `schemas/headless-config.v1.json` | Low -- additive optional field |
| `HeadlessTool.InputMode` property | API Designer | `src/Andy.Cli/HeadlessConfig/HeadlessRunConfig.cs` | Low -- null/absent default |
| `CliSubprocessTool` JSON mode branch | Implementer | `src/Andy.Cli/Headless/Tools/CliSubprocessTool.cs` | Low -- new code path, existing path untouched |
| `BuildMetadata` switch | Implementer | `src/Andy.Cli/Headless/Tools/CliSubprocessTool.cs` | Low -- static per-config |
| Unit tests for JSON mode | Implementer | `tests/Andy.Cli.Tests/` | Low -- mirrors existing argv tests |
| Integration test with a JSON-reading fixture | Implementer | `tests/Andy.Cli.Tests/` | Medium -- needs a small JSON-reading script |
| Doc updates | API Designer | `docs/headless-runtime.md`, `docs/tool-execution-architecture.md`, `schemas/samples/` | Low -- additive |

## 7. Out of Scope

- Changing the interactive CLI tool surface (not a headless concern).
- Modifying `McpRemoteTool` (already JSON-native).
- Adding JSON Schema reflection from CLI tool config to `ToolMetadata.Parameters`
  beyond the `object` type declaration. A follow-on can map per-tool schemas
  when config authors need stronger LLM argument shaping.
- Supporting both argv and JSON simultaneously in a single tool invocation.
  The `input_mode` is per-binding, not per-call.

## 8. Decision Required

- [x] Approve the schema change as additive (no version bump to v2).
- [x] Approve `input_mode` defaulting to `"argv"` for backward compatibility.
- [x] Confirm stdin-based JSON transport (vs argv blob).
- [ ] Assign implementation to the next sprint.
