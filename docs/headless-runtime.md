# Headless runtime

## Concept: andy-cli's dual role

andy-cli has two roles that share a single binary:

1. **Interactive assistant.** The terminal UI a developer drives directly.
2. **Headless agent runtime.** A non-interactive execution mode driven entirely
   by a config file. This is the mode that
   [andy-containers](https://github.com/rivoli-ai/andy-containers) spawns for the
   conductor platform.

The second role was established by the 2026-04-21 architectural decision recorded
in [Epic AQ (rivoli-ai/andy-cli#44)](https://github.com/rivoli-ai/andy-cli/issues/44)
and is documented in
[docs/adr/0001-headless-agent-runtime.md](adr/0001-headless-agent-runtime.md).
Rather than build a second agent host, the conductor platform reuses the same
andy-cli binary it already ships, invoked in headless mode. The container runtime
hands andy-cli a fully resolved config (agent prompt, model, tools, limits) and
consumes a structured NDJSON event stream from stdout. The numeric process exit
code is the run's terminal status.

The two roles, the config contract, and the event contract are versioned so that
andy-cli and andy-containers can roll independently. See the
[ADR](adr/0001-headless-agent-runtime.md) for the versioning rationale.

## The command

```
andy-cli run --headless --config <path>
```

Argument parsing lives in
`src/Andy.Cli/HeadlessConfig/HeadlessRunner.cs` (`ParseArgs`). The dispatcher
passes `run` as `args[0]`; the runner parses the remainder:

| Flag | Required | Meaning |
| --- | --- | --- |
| `--headless` | Yes | Selects non-interactive execution. If omitted, the run fails with a config error: interactive `andy-cli run` without `--headless` is not supported. |
| `--config <path>` | Yes | Path to a `headless-config.v1` JSON file. `--config` with no following token is an error. |

Any unrecognized token is rejected (`Unknown argument: <token>`). All argument
errors, a missing `--headless`, and a missing `--config` produce exit code
`2` (ConfigError) and print usage to stderr.

After parsing, `HeadlessRunner.RunAsync` loads and validates the config via
`HeadlessConfigLoader.TryLoadAsync`. A load/validation failure also returns exit
code `2`. On success it delegates to `HeadlessAgentRunner.ExecuteAsync`, which
runs the agent loop and emits the [event stream](event-stream.md).

## Config schema walkthrough

The authoritative contract is
[`schemas/headless-config.v1.json`](../schemas/headless-config.v1.json); the
strongly-typed view is `HeadlessRunConfig` in
`src/Andy.Cli/HeadlessConfig/HeadlessRunConfig.cs`. The object is closed
(`additionalProperties: false`) at every level.

### Top-level fields

| Field | Required | Type | Notes |
| --- | --- | --- | --- |
| `schema_version` | Yes | const `1` | Schema version pin. A future bump to `2` introduces a new schema file (`v2.json`); v1 is never overloaded. |
| `run_id` | Yes | string (uuid) | Correlates stdout events and artifacts with the andy-containers Run entity. |
| `mcp_gateway` | No | string (uri) | Base URL for MCP tool endpoints. When present, MCP tool bindings without an explicit `endpoint` have their endpoint resolved as `{mcp_gateway}/{tool-name}`. Supports `$ANDY_MCP_URL` substitution. Per-tool `endpoint` overrides this when both are present. |
| `agent` | Yes | object | Agent identity and resolved system prompt. See below. |
| `model` | Yes | object | LLM provider and model selection. See below. |
| `tools` | Yes | array | Concrete tool bindings the run may call. May be empty. See [Tool transports](#tool-transports). |
| `workspace` | Yes | object | Mounted workspace root and optional branch. |
| `env_vars` | No | object (string to string) | Extra env vars injected into the agent process. Reserved names (below) must not be shadowed. |
| `output` | Yes | object | Where the final output file goes and where the event stream goes. |
| `event_sink` | No | object | NATS subject and/or FIFO path metadata. |
| `policy_id` | No | string (uuid) | Resolved policy UUID from andy-rbac. |
| `boundaries` | No | array of strings | Policy-derived guard-rail tags (e.g. `read-only`, `no-prod`). Authoritative evaluation happens server-side at each tool call. |
| `limits` | Yes | object | Iteration and wall-clock caps. |

### `agent`

Required: `slug`, `instructions`.

| Field | Required | Notes |
| --- | --- | --- |
| `slug` | Yes | Agent identifier. Lowercase ASCII, dash-separated, 2-64 chars (`^[a-z][a-z0-9-]{1,63}$`). |
| `revision` | No | Integer >= 1; pins a specific agent revision. Absent = head. |
| `instructions` | Yes | The resolved system prompt (agent instructions + delegation objective), 1-100000 chars. Used verbatim as the `SimpleAgent` system prompt. |
| `output_format` | No | Free-form hint for the agent's final output, e.g. `plain` or `json-triage-output-v1`. |

### `model`

Required: `provider`, `id`.

| Field | Required | Notes |
| --- | --- | --- |
| `provider` | Yes | One of `anthropic`, `openai`, `openrouter`, `google`, `cerebras`, `groq`, `local`. |
| `id` | Yes | Model identifier as recognized by the provider (e.g. `claude-sonnet-4-6`). Non-empty. |
| `api_key_ref` | No | How to resolve the API key. Supported prefix today: `env:NAME` (read an env var). `secret-store:NAME` is reserved for the future. Never the bare key. |

Key resolution note: the headless runner resolves providers through Andy.Llm's
factory, which reads provider API keys from environment variables
(`ConfigureLlmFromEnvironment`). In practice the env var referenced by
`api_key_ref` must be present in the process environment. Resolution schemes
beyond `env:` are not yet implemented (see `ResolveLlmProvider` in
`HeadlessAgentRunner.cs`).

Model threading note: `ConfigureLlmFromEnvironment` only populates each
provider's model from env vars (e.g. `OPENROUTER_MODEL`). Some providers,
OpenRouter among them, require a model at construction time. The headless runner
therefore threads `model.id` into the provider config the factory will resolve
for the run (`HeadlessAgentRunner.ApplyConfiguredModel`), so the config's model
selection always wins.

### `workspace`

Required: `root`.

| Field | Required | Notes |
| --- | --- | --- |
| `root` | Yes | Absolute path inside the container to the mounted workspace (typically `/workspace`). |
| `branch` | No | Git branch the run operates on. |

### `output`

Required: `file`, `stream`.

| Field | Required | Notes |
| --- | --- | --- |
| `file` | Yes | Path to write the final output. Written atomically (temp file + rename) only after a successful run; absent on failure before the output stage. |
| `stream` | Yes | `stdout` (default) or `fifo`. With `fifo`, the path is named in `event_sink.path`. |

### `event_sink`

Optional, closed object.

| Field | Notes |
| --- | --- |
| `nats_subject` | NATS subject the container runtime fans events to (`^andy\.containers\.events\.run\.<run_id>\.<name>$`). The agent does not publish directly. |
| `path` | Absolute path to a named FIFO when `output.stream == "fifo"`. |

### `limits`

Required: `max_iterations`, `timeout_seconds`.

| Field | Required | Range | Notes |
| --- | --- | --- | --- |
| `max_iterations` | Yes | 1-10000 | Hard cap on agent loop turns. Exhausting it maps to exit code `4` (Timeout). |
| `timeout_seconds` | Yes | 1-86400 | Wall-clock timeout. Exceeding it maps to exit code `4` (Timeout). |

## Worked example: OpenRouter + Mimo

A complete, valid v1 config using OpenRouter with the Mimo model. OpenRouter
requires a model at construction time; the runner threads `model.id` into the
provider config, so `model.id` is what is used. MCP tools use the top-level
`mcp_gateway` field so the runtime resolves each tool endpoint as
`{mcp_gateway}/{tool-name}`.

```json
{
  "schema_version": 1,
  "run_id": "f6c2b0d4-2c1e-4a3f-9b21-2f7e0c5d8a90",
  "mcp_gateway": "$ANDY_MCP_URL",
  "agent": {
    "slug": "code-reviewer",
    "instructions": "You are a code reviewer. Review the changes on the working branch and produce a concise findings report. Objective: review the open diff and report correctness issues.",
    "output_format": "plain"
  },
  "model": {
    "provider": "openrouter",
    "id": "xiaomi/mimo-v2.5",
    "api_key_ref": "env:OPENROUTER_API_KEY"
  },
  "tools": [
    {
      "name": "read_file",
      "transport": "mcp"
    },
    {
      "name": "andy-tasks-cli",
      "transport": "cli",
      "binary": "andy-tasks-cli",
      "command": ["--json"]
    }
  ],
  "workspace": {
    "root": "/workspace",
    "branch": "feature/review"
  },
  "output": {
    "file": "/workspace/.andy/output.txt",
    "stream": "stdout"
  },
  "limits": {
    "max_iterations": 40,
    "timeout_seconds": 1800
  }
}
```

Run it with:

```
OPENROUTER_API_KEY=sk-or-... andy-cli run --headless --config /workspace/.andy/run.json
```

See [`schemas/samples/`](../schemas/samples) for additional ready-to-read
example configs (coding, planning, triage).

## Exit codes

The contract is `HeadlessExitCode` in
`src/Andy.Cli/HeadlessConfig/HeadlessExitCode.cs`. It is shared with
andy-containers, which keys off these values for retry/cancel/report semantics,
so the numeric values are load-bearing and must not be reordered.

| Code | Name | Meaning |
| --- | --- | --- |
| 0 | Success | Run completed; output file written. |
| 1 | AgentFailure | The agent loop ran but did not converge (non-timeout LLM/tool failure); also covers tool-wiring, provider-resolution, and output-write failures. |
| 2 | ConfigError | Config missing, unparseable, or invalid, or bad CLI args. |
| 3 | Cancelled | Cooperative cancellation (e.g. SIGTERM) before completion. |
| 4 | Timeout | Wall-clock `timeout_seconds` or `max_iterations` exceeded. |
| 5 | InternalError | Unexpected internal error (bug); should be rare. |

The same code is mirrored in the final `finished` event's `exit_code` field on
the [event stream](event-stream.md).

## Tool transports

`tools[]` is a list of concrete bindings the run is allowed to call.
`HeadlessToolHost` (`src/Andy.Cli/Headless/HeadlessToolHost.cs`) turns each entry
into an `ITool` adapter and registers it; no built-in tools are registered, so
the agent's tool surface is exactly what the config lists. Two transports are
supported.

### `cli`

Required: `name`, `transport: "cli"`, `binary`.

| Field | Notes |
| --- | --- |
| `name` | Tool identifier as seen by the agent (`^[a-z][a-z0-9_.-]*$`). |
| `binary` | Executable name resolved via `$PATH` (e.g. `andy-tasks-cli`). |
| `command` | Optional default argv the agent prepends; the agent appends its own args. |
| `input_mode` | Optional. `"argv"` (default): LLM parameters are flattened into a string array appended to `command[]`. `"json"`: LLM parameters are serialized as a JSON object and written to the subprocess's stdin, then stdin is closed. The subprocess reads the JSON from stdin. Exit/error semantics are unchanged. |

Each `cli` binding becomes a `CliSubprocessTool`.

#### JSON input mode

When `input_mode` is set to `"json"`, the CLI tool transport switches from positional
argv passing to structured stdin-based JSON bridging:

1. The LLM emits an `arguments` object (same parameter name as MCP tools):
   `{"arguments": {"query": "open bugs", "limit": 10}}`
2. `CliSubprocessTool` serializes the object as JSON (UTF-8, no BOM, preserving the
   LLM's property names verbatim).
3. The JSON bytes are written to the subprocess's stdin via `process.StandardInput.BaseStream`.
4. stdin is closed (sends EOF) so the subprocess knows the input is complete.
5. The subprocess reads JSON from stdin, processes it, and writes its result to stdout.
6. stdout and stderr are captured as usual; exit code 0 = success.

The JSON payload is bounded to 1 MB. Payloads exceeding this limit return a failure
rather than writing to the pipe.

This mode gives CLI tools the same structured-input mental model as MCP tools, reducing
the cognitive split between "MCP tools get JSON, CLI tools get strings" that complicates
agent instructions.

### `mcp`

Required: `name`, `transport: "mcp"`.
Optional: `endpoint` (required if no top-level `mcp_gateway` is set).

| Field | Notes |
| --- | --- |
| `name` | Tool identifier; must match a tool advertised by the endpoint, or the run fails. |
| `endpoint` | mcp-gateway URL for this tool (usually `$ANDY_MCP_URL/<tool-name>`). Must be a valid URI. When absent and a top-level `mcp_gateway` is set, resolved as `{mcp_gateway}/{tool-name}`. |

`HeadlessToolHost` opens one `McpClient` per distinct endpoint URL (adapters that
share an endpoint reuse a single connection), lists the remote tools, and binds
each `mcp` entry to the matching remote tool via `McpRemoteTool`. If the endpoint
does not advertise a tool with the configured `name`, the run fails fast with a
clear error rather than letting the LLM silently never call it.

### Reserved environment variables

The container runtime always sets these; `env_vars` in the config must not
shadow them:

| Variable | Purpose |
| --- | --- |
| `ANDY_MCP_URL` | Base URL of the mcp-gateway. Primary consumer is the `mcp_gateway` top-level config field, which resolves tool endpoints as `{mcp_gateway}/{tool-name}`. Legacy per-tool `endpoint` values may also reference it directly. |
| `ANDY_TOKEN` | Run-scoped bearer token. When set, `HeadlessToolHost` attaches it as `Authorization: Bearer <token>` on every MCP request. The config cannot override it. |
| `ANDY_PROXY_URL` | Egress proxy URL injected by the container runtime. |

## See also

- [Event stream](event-stream.md) — the NDJSON event contract.
- [ADR 0001: headless agent runtime](adr/0001-headless-agent-runtime.md) — why one binary hosts both modes, and the versioning strategy.
- [`schemas/headless-config.v1.json`](../schemas/headless-config.v1.json) — the config schema.
- [`schemas/samples/`](../schemas/samples) — example configs.
