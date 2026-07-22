# 1. Headless agent runtime

## Status

Accepted (2026-04-21). Epic AQ (rivoli-ai/andy-cli#44).

## Context

The conductor platform (rivoli-ai/andy-containers) needs to spawn agents
non-interactively inside containers: given a resolved agent prompt, a model, a
set of tools, and resource limits, run the agent loop to completion and report a
structured result plus a terminal status.

andy-cli already implements an agent loop, a tool registry, MCP and CLI tool
adapters, and provider resolution through Andy.Llm. Building a separate headless
agent host would duplicate that machinery and create two implementations of the
same agent semantics that would inevitably drift.

At the same time, andy-cli's primary identity is an interactive terminal
assistant. A headless mode has different needs: no terminal UI, input entirely
from a config file, output as a machine-readable event stream and an output
file, and a numeric exit code that an orchestrator can map to a run state.

The two surfaces are also owned by different repositories that release on
different cadences. andy-containers must be able to depend on a stable contract
(config in, events and exit code out) without being coupled to andy-cli's
internal release schedule.

## Decision

**One binary, two modes.** andy-cli hosts both the interactive assistant and the
headless agent runtime. The headless mode is selected by
`andy-cli run --headless --config <path>`; `--headless` is required, and
interactive `run` without it is not supported. Headless execution is driven
entirely by the config file and reuses andy-cli's existing agent loop, tool
adapters, and provider resolution.

**Versioned cross-repo contracts.** The boundary between andy-cli and
andy-containers is defined by three explicitly versioned artifacts rather than by
shared code:

- The config schema, `schemas/headless-config.v1.json`, carries a
  `schema_version` field pinned to `1`.
- The event-stream schema, `schemas/headless-events.v1.json`, carries the same
  `schema_version` pin on every emitted line.
- The process exit codes (`HeadlessExitCode`) are a fixed, ordered enum that
  andy-containers keys off for retry/cancel/report semantics.

Within a major version the schemas are **additive**: new optional fields and new
event kinds may be introduced, and consumers must tolerate unknown fields and
unknown event kinds. A breaking change ships as a new schema file (for example
`headless-config.v2.json`) and a `schema_version` of `2`, with v1 left intact so
older producers and consumers keep working during migration. v1 is never
overloaded with incompatible meanings.

## Consequences

- A single agent implementation backs both interactive and headless use; agent
  behavior cannot drift between the two modes.
- andy-containers integrates against file, stdout, and exit-code contracts, not
  against andy-cli internals, so the two repositories can release independently.
- The `schema_version` pin and the additive-evolution rule give both sides a
  clear, low-risk path to extend the contract and an unambiguous signal
  (a new file, a bumped version) when a breaking change arrives.
- The exit-code enum's numeric values are load-bearing and must not be reordered
  or repurposed, because andy-containers depends on them.
- Carrying two modes in one binary means headless concerns (no TTY, structured
  output, env-driven configuration) must be respected in shared code paths; the
  headless runner keeps its tool surface and provider wiring explicit and
  self-contained to limit coupling to interactive defaults.

## References

- Epic AQ: rivoli-ai/andy-cli#44
- `docs/headless-runtime.md` - the command, config schema, and exit codes.
- `docs/event-stream.md` - the NDJSON event contract.
- `schemas/headless-config.v1.json`, `schemas/headless-events.v1.json`.
