# 2. Headless v1 inactive fields: implement or reject

## Status

Accepted (2026-07-17). rivoli-ai/andy-cli#180.

## Context

The published headless v1 contract (`schemas/headless-config.v1.json`,
`docs/headless-runtime.md`) exposed several fields that the runtime did not
meaningfully apply. An audit of the runtime against the contract found the
following:

| Field | Contract implied | Actual v1 runtime (before #180) |
| --- | --- | --- |
| `schema_version`, `run_id`, `agent.slug`, `agent.instructions`, `model.provider`, `model.id`, `tools`, `workspace.root`, `output.file`, `limits.*` | Applied | Applied |
| `permissions.allowed_tools` | Relaxes fail-closed tool denials | Applied (AX.4) |
| `agent.output_format` | Hint for the final output | No-op: never read |
| `model.api_key_ref` | Resolve the API key (`env:NAME`) | No-op: key read from provider env var only; `api_key_ref` ignored |
| `env_vars` | Injected into the agent process | No-op: never set |
| `workspace.branch` | Branch the run operates on | No-op: never read or verified |
| `output.stream: fifo` / `event_sink.path` | Redirect the event stream to a FIFO | No-op: events always went to stdout; no cross-field requirement |
| `policy_id` | Resolved RBAC policy | No-op: never read |
| `boundaries` | Guard-rail tags (`read-only`, `no-prod`) | No-op: never read or enforced |
| `agent.revision`, `event_sink.nats_subject` | Metadata for the container runtime | Metadata (consumed outside andy-cli) |

Two classes of problem followed:

1. **False security assurance.** `policy_id` and `boundaries` read as security
   controls. A caller could set `boundaries: ["read-only"]` and reasonably
   believe the run was constrained, while the runtime applied nothing. A no-op
   security field is worse than an absent one: it invites reliance on a guarantee
   that does not exist.

2. **Silent divergence.** `output_format`, `api_key_ref`, `env_vars`,
   `workspace.branch`, and FIFO streaming were documented as behavior but did
   nothing. The schema also failed to encode the cross-field requirement that
   FIFO streaming needs a sink path, and the docs contradicted the runtime about
   built-in tool registration (docs said none were registered; the runtime
   registers the full built-in catalog, fail-closed).

## Decision

**No v1 field is a silent no-op.** Each is either enforced end-to-end or the
config that carries it is rejected at load time with a clear, actionable,
secret-free message. Safety is the default: closing off an unenforceable field is
preferred over a fake implementation.

Per field:

- **`policy_id`, `boundaries` - REMOVED and REJECTED.** No runtime policy/boundary
  engine exists to make them enforceable, and leaving them created false security
  assurance. They are removed from the v1 schema; a config carrying either is
  rejected (`additionalProperties: false`), with the loader emitting a targeted
  message pointing to `permissions.allowed_tools` (the one enforceable per-run
  tool control) and to this ADR. A real policy engine, if built, arrives as a new
  schema version, never by reviving these as v1 no-ops.

- **`agent.output_format` - ENFORCED.** A label beginning with `json`
  (case-insensitive) requires the final output to parse as JSON. On failure the
  run exits `1` and writes no output file, so a downstream consumer never
  receives malformed structured output. Non-`json` labels remain free-form.

- **`model.api_key_ref` - IMPLEMENTED for `env:NAME`, REJECTED otherwise.** The
  `env:NAME` value is loaded into the provider's API key. `secret-store:` and bare
  values are rejected (schema `pattern` plus validator). Secret values are never
  logged, emitted, or echoed in error messages.

- **`env_vars` - IMPLEMENTED with reserved-name protection.** Entries are set into
  the process environment before provider/tool wiring. Shadowing a reserved
  runtime variable (`ANDY_PROXY_URL`, `ANDY_TOKEN`, `ANDY_MCP_URL`) is rejected so
  a run cannot repoint its egress proxy, spoof its run token, or redirect the MCP
  gateway.

- **`workspace.branch` - VERIFIED.** When set, the runtime confirms
  `workspace.root` is a git work tree currently on that branch and fails fast on a
  mismatch or an unverifiable tree. It does not itself check out or switch
  branches; the container runtime owns checkout. Verification turns a former
  no-op into a guard-rail against running on the wrong code.

- **`output.stream: fifo` - IMPLEMENTED with a cross-field requirement.** The
  schema now requires `event_sink.path` when `stream` is `fifo` (top-level
  `allOf`/`if`-`then`), and the runtime opens that path and streams every event to
  it instead of stdout.

- **Atomic output - hardened.** The temp file used for the atomic write is cleaned
  up if the write fails or is cancelled mid-flight, so failures leave no stray
  `*.tmp.*` files.

- **Docs corrected.** `docs/headless-runtime.md` now states that the built-in tool
  catalog IS registered and is permission-gated fail-closed, documents the
  enforcement of every field above, and states the versioning rule.

## Consequences

- Configs are safe by default: an unenforceable control cannot be silently
  ignored. Callers relying on `policy_id`/`boundaries` get a loud, early failure
  with a migration pointer instead of a false sense of protection.

- Removing `policy_id`/`boundaries` is technically a breaking change to a v1
  schema, which ADR 0001 says should be additive within a major version. This is a
  deliberate, documented exception: the fields never functioned, and eliminating
  false security assurance outweighs strict additivity. Downstream producers
  (andy-containers, conductor) must stop emitting them; the failure is immediate
  and self-describing.

- The contract library (`Andy.Cli.Headless.Contract`) shares the same embedded
  schema, so downstream generators that validate before launch get the same
  rejections at generation time, not at container runtime.

- Enforceable per-run tool control in v1 is exactly `permissions.allowed_tools`.
  A future richer policy model ships as `headless-config.v2.json` with
  `schema_version: 2`.

## References

- rivoli-ai/andy-cli#180
- [ADR 0001: headless agent runtime](0001-headless-agent-runtime.md)
- `docs/headless-runtime.md` - field enforcement summary and versioning.
- `schemas/headless-config.v1.json` - the config schema.
- `src/Andy.Cli/HeadlessConfig/HeadlessConfigValidator.cs`,
  `src/Andy.Cli/HeadlessConfig/HeadlessConfigLoader.cs`,
  `src/Andy.Cli/Headless/HeadlessAgentRunner.cs` - the enforcement points.
