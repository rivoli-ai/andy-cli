# Event stream

The headless runtime emits a structured event stream while it runs. The format
is NDJSON: one JSON object per line. The authoritative contract is
[`schemas/headless-events.v1.json`](../schemas/headless-events.v1.json); the
writer is `HeadlessEventEmitter` in
`src/Andy.Cli/Headless/HeadlessEventEmitter.cs`.

## Destination

Events are written to **stdout** by default (`output.stream == "stdout"`). When
`output.stream == "fifo"`, the destination is the named FIFO at
`event_sink.path`. The writer is flushed after every event so consumers see each
line as soon as it is produced.

andy-containers consumes this stream and fans it out to the NATS subject
`andy.containers.events.run.<run_id>.progress` (the agent does not publish to
NATS directly).

The stream is **additive**: consumers must tolerate unknown `kind` values and
unknown `data` fields. New event kinds or fields may be added within v1 without
breaking consumers.

## Envelope

Every line is an object with this envelope (all four fields required):

| Field | Type | Notes |
| --- | --- | --- |
| `schema_version` | integer, const `1` | Schema version pin. v2 ships a new file. |
| `ts` | string (RFC 3339 / ISO 8601 with offset) | Wall-clock UTC from the agent process, e.g. `2026-04-25T22:31:14.123+00:00`. |
| `kind` | string enum | One of the event kinds below. |
| `data` | object | Per-kind payload (see below). |

Wire field names are `snake_case`. Fields whose value is null are omitted.

## Event kinds

The runner always emits `started` first and `finished` last, even on failure, so
every run produces a clean envelope. The kinds, in
[enum order](../schemas/headless-events.v1.json):

### `started`

Emitted once, immediately before the agent loop begins.

| Field | Type | Notes |
| --- | --- | --- |
| `run_id` | string (uuid) | Echoes `config.run_id`. |
| `agent_slug` | string | From `config.agent.slug`. |
| `model_provider` | string | From `config.model.provider`. |
| `model_id` | string | From `config.model.id`. |
| `tool_count` | integer >= 0 | Number of tool bindings registered. |

### `llm_chunk`

Incremental LLM output text. Optional; emitted when streaming is surfaced.

| Field | Type | Notes |
| --- | --- | --- |
| `text` | string | The chunk of generated text. |
| `turn` | integer >= 0 | Optional turn index. |

### `tool_call_started`

Emitted when the agent invokes a tool.

| Field | Type | Notes |
| --- | --- | --- |
| `call_id` | string | Producer-assigned id, unique within the run; pairs with the matching `tool_call_finished`. |
| `tool_name` | string | The tool the agent called. |
| `args_digest` | string | Optional. SHA-256 hex of the canonical-JSON-serialized args. Raw args are never emitted, to keep the stream cheap and avoid leaking secrets. |

### `tool_call_finished`

Emitted when a tool call completes, paired by `call_id` with its
`tool_call_started`.

| Field | Type | Notes |
| --- | --- | --- |
| `call_id` | string | Pairs with the `tool_call_started`. |
| `tool_name` | string | The tool that finished. |
| `ok` | boolean | `true` only when `outcome=success`. Any non-success terminal state is `ok=false`. |
| `outcome` | string | Optional. Terminal state: `success`, `failed`, `denied`, `timed_out`, or `cancelled`. Consumers must tolerate its absence and unknown values. |
| `duration_ms` | integer >= 0 | Measured call duration (start-to-finish). Never fabricated as 0. |
| `result_digest` | string | Optional. SHA-256 hex of the canonical-JSON result. May be omitted on error. |
| `error` | string | Optional short error string when `ok=false`. Consumers must not key on its content. |

Implementation note (rivoli-ai/andy-cli#179): the runner observes the ACTUAL
tool execution and emits `tool_call_finished` only after the tool truly
completes. The engine's `SimpleAgent` raises `ToolCalled` BEFORE the tool is
permission-evaluated or executed and exposes no post-execution event, so the
CLI instead wraps the `IToolExecutor` it hands to the agent (see
`ObservingToolExecutor`): the engine calls that executor exactly once per tool
call and awaits the real result or exception. That boundary gives an exact
start/finish correlation, a measured `duration_ms`, and the real `outcome`:

- `success` -> `ok=true`.
- `failed` -> the tool ran but returned an unsuccessful result or threw.
- `denied` -> the permission engine refused the call (distinct from `failed`).
- `timed_out` -> a per-tool timeout / resource-limit abort.
- `cancelled` -> the run/caller cancelled while the tool was running.

A denied or still-running tool is never reported `ok=true`.

Cross-repo remainder: a full engine-native `ToolCompleted`/`ToolResult` event
on `SimpleAgent` (andy-engine) would let hosts that do NOT own the executor
observe completion. Until then the CLI-owned executor wrap is the exact signal.

### `output_written`

Emitted after the final output file is written atomically.

| Field | Type | Notes |
| --- | --- | --- |
| `path` | string | Absolute path to the written file. |
| `bytes` | integer >= 0 | Byte length written. |

### `error`

Emitted for a run error. A fatal error is followed by `finished` and a non-zero
exit.

| Field | Type | Notes |
| --- | --- | --- |
| `message` | string | The error message. |
| `fatal` | boolean | Optional. `true` means terminal: the next event is `finished` and the process exits non-zero. |

### `finished`

Emitted once, last, on every run.

| Field | Type | Notes |
| --- | --- | --- |
| `exit_code` | integer 0-5 | Mirrors the process exit code; see the [exit codes table](headless-runtime.md#exit-codes). |
| `duration_ms` | integer >= 0 | Total run duration. |
| `iterations` | integer >= 0 | Optional. Number of LLM/tool turns executed before terminating. |

## Digests

`args_digest` and `result_digest` are SHA-256 hashes of the canonical
`snake_case` JSON of the args/result, prefixed `sha256:` (computed by
`HeadlessEventEmitter.ComputeDigest`). They let consumers correlate or dedupe
without the producer emitting raw payloads that might contain secrets.

## Sample stream

A typical successful run with one tool call (NDJSON; one object per line):

```
{"schema_version":1,"ts":"2026-04-25T22:31:14.001+00:00","kind":"started","data":{"run_id":"f6c2b0d4-2c1e-4a3f-9b21-2f7e0c5d8a90","agent_slug":"code-reviewer","model_provider":"openrouter","model_id":"xiaomi/mimo-v2.5","tool_count":2}}
{"schema_version":1,"ts":"2026-04-25T22:31:15.220+00:00","kind":"tool_call_started","data":{"call_id":"a1b2c3d4e5f6","tool_name":"read_file"}}
{"schema_version":1,"ts":"2026-04-25T22:31:15.246+00:00","kind":"tool_call_finished","data":{"call_id":"a1b2c3d4e5f6","tool_name":"read_file","ok":true,"outcome":"success","duration_ms":26}}
{"schema_version":1,"ts":"2026-04-25T22:31:18.540+00:00","kind":"output_written","data":{"path":"/workspace/.andy/output.txt","bytes":2048}}
{"schema_version":1,"ts":"2026-04-25T22:31:18.541+00:00","kind":"finished","data":{"exit_code":0,"duration_ms":4540,"iterations":3}}
```

A failing run (e.g. timeout) ends with a fatal `error` then `finished`:

```
{"schema_version":1,"ts":"2026-04-25T22:31:14.001+00:00","kind":"started","data":{"run_id":"f6c2b0d4-2c1e-4a3f-9b21-2f7e0c5d8a90","agent_slug":"code-reviewer","model_provider":"openrouter","model_id":"xiaomi/mimo-v2.5","tool_count":2}}
{"schema_version":1,"ts":"2026-04-25T23:01:14.002+00:00","kind":"error","data":{"message":"Agent loop exceeded timeout_seconds=1800.","fatal":true}}
{"schema_version":1,"ts":"2026-04-25T23:01:14.003+00:00","kind":"finished","data":{"exit_code":4,"duration_ms":1800001,"iterations":12}}
```

## See also

- [Headless runtime](headless-runtime.md) — the command, config schema, and exit codes.
- [ADR 0001: headless agent runtime](adr/0001-headless-agent-runtime.md) — versioning strategy for this contract.
- [`schemas/headless-events.v1.json`](../schemas/headless-events.v1.json) — the event schema.
