# Andy CLI headless runtime

`andy-cli` has two modes:

- **Interactive** (existing): `andy-cli` — TTY-driven, user-facing. The standalone assistant.
- **Headless** (introduced by Epic AQ, rivoli-ai/andy-cli#44): `andy-cli run --headless --config <path>` — non-interactive, spawned by rivoli-ai/andy-containers' run configurator (Epic AP). Emits a structured event stream, writes an output file, exits with a typed status code.

This document covers the headless contract: the config schema (AQ1), the event stream (AQ3), the output contract (AQ4), and the exit semantics (AQ2/AQ5).

## Config schema — v1

**Canonical spec:** [`schemas/headless-config.v1.json`](../schemas/headless-config.v1.json) (JSON Schema draft 2020-12, `$id = https://rivoli-ai.com/schemas/andy-cli/headless-config.v1.json`).

**Samples:** [`schemas/samples/*.json`](../schemas/samples/) — three fixtures covering the `triage-agent`, `planning-agent`, and `coding-agent` shapes. Each validates against the schema via [`HeadlessConfigSchemaTests`](../tests/Andy.Cli.Tests/HeadlessConfig/HeadlessConfigSchemaTests.cs).

### Shape

```jsonc
{
  "schema_version": 1,                    // const; v2 ships a new file
  "run_id": "uuid",                       // correlates with andy-containers Run (#103)
  "agent": {
    "slug": "triage-agent",               // from andy-agents Epic W
    "revision": 3,                        // optional version pin
    "instructions": "...",                // system prompt (agent base + delegation contract.objective)
    "output_format": "json-triage-v1"     // optional label or schema ref
  },
  "model": {
    "provider": "anthropic",              // enum: anthropic|openai|google|cerebras|local
    "id": "claude-sonnet-4-6",
    "api_key_ref": "env:ANDY_MODEL_KEY"   // resolve from env / secret-store
  },
  "tools": [                              // resolved from agent.skills × allowed_tools
    { "name": "issues.get", "transport": "mcp", "endpoint": "https://mcp/issues.get" },
    { "name": "repo.search", "transport": "cli", "binary": "andy-issues-cli", "command": ["andy-issues-cli", "search"] }
  ],
  "workspace": { "root": "/workspace", "branch": "main" },
  "env_vars": { "RIVOLI_TENANT_ID": "..." },
  "output":   { "file": "/workspace/.andy-run/output.json", "stream": "stdout" },
  "event_sink": {
    "nats_subject": "andy.containers.events.run.{run_id}.progress"
  },
  "policy_id": "uuid",                    // andy-rbac Epic V
  "boundaries": ["read-only"],
  "limits": { "max_iterations": 50, "timeout_seconds": 300 }
}
```

### Field notes

- **`schema_version`** — bumping to v2 introduces a new schema file (`v2.json`) and a compatibility layer in the loader. Do not overload v1.
- **`agent.slug`** — lowercase slugs matching `^[a-z][a-z0-9-]{1,63}$`. Enforced by schema.
- **`tools[].transport`** — only `mcp` and `cli` are supported. MCP calls go through mcp-gateway (Epic Y); CLI transports exec the named binary as a subprocess.
- **`output.stream`** — `stdout` (default) or `fifo`. When `fifo`, the path is in `event_sink.path`; andy-containers creates the FIFO before spawning the agent.
- **`event_sink.nats_subject`** — **informational**. The agent does not publish to NATS directly. andy-containers (Epic AP6) fans agent stdout → NATS subject.
- **`boundaries`** — tags derived from the bound policy; runtime guard-rails, not authoritative. Real policy evaluation happens server-side at each tool call (per Epic V).
- **`limits.max_iterations`** — hard cap on agent loop iterations. Exceeded → exit code 4.
- **`limits.timeout_seconds`** — wall-clock timeout. Exceeded → exit code 4.

### Reserved env vars (injected by container runtime, Epic Y5)

- `ANDY_PROXY_URL` — unified proxy base URL.
- `ANDY_TOKEN` — run-scoped auth token (Epic Y6 lifecycle).
- `ANDY_MCP_URL` — mcp-gateway base URL.

These are guaranteed present by the container image; the config's `env_vars` object MUST NOT shadow them.

## Invocation

```sh
andy-cli run --headless --config /workspace/.andy-run/config.json
```

Exit codes (pinned by AQ2):

| Code | Meaning |
|---|---|
| 0 | Success — output file written, event stream complete |
| 1 | Agent-level failure (LLM refused, tool-call chain failed, output validation failed) |
| 2 | Config error (schema violation, missing required files, unresolvable api_key_ref) |
| 3 | Cancelled (SIGTERM or `andy-cli cancel --run-id`) |
| 4 | Timeout (`max_iterations` or `timeout_seconds` exceeded) |
| 5 | Internal error (bug) |

## Event stream (AQ3)

Each agent step emits one NDJSON line: `{ts, kind, data}`. Kinds (to be pinned in AQ3):

- `started` — run starting; payload carries resolved model / tool list.
- `tool_call_started` / `tool_call_finished` — tool invocations with args/result digests.
- `llm_chunk` — streaming LLM output (if `output_format: plain` or streaming is enabled).
- `output_written` — final output file written and fsynced.
- `error` — recoverable or terminal error.
- `finished` — run complete (always emitted before exit, even on failure).

Full event schema lands with AQ3 (`schemas/headless-events.v1.json`).

## Output file (AQ4)

- Written to `output.file` path from config.
- Atomic write (tmp + rename).
- Free-form text OR structured JSON matching the `output_format` schema hint.
- On non-zero exit the file MAY be absent (no partial writes for structured outputs).

## Cross-repo links

| What | Where |
|---|---|
| Epic AQ (this epic) | rivoli-ai/andy-cli#44 |
| AQ1 — config schema | rivoli-ai/andy-cli#46 |
| Run entity (the thing correlated by `run_id`) | rivoli-ai/andy-containers#103 |
| Epic AP — configurator + headless runner | rivoli-ai/andy-containers#101 |
| Epic Y5 — container env injection | rivoli-ai/andy-mcp-gateway#8 |
| Epic Y6 — run-scoped token lifecycle | rivoli-ai/andy-mcp-gateway#9 |
| Agent spec source of truth | rivoli-ai/andy-agents Epic W |
