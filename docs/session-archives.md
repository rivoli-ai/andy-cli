# Session archives, forks, and usage stats

Andy persists every interactive conversation under `~/.andy/sessions/<id>.json` so it can be
resumed later (`--resume` / `--continue`, `/resume`). This document covers what was added on
top of that: moving a session between machines, branching one at an earlier turn, titling it,
and inspecting aggregate token usage.

ACP session catalog, delete, resume, and replay are a separate surface and are not described
here.

## Commands

The same command backs the interactive slash command and the noninteractive CLI:

| Interactive | Noninteractive |
| --- | --- |
| `/session export [id] [--out path] [--markdown] [--tools] [--metadata]` | `andy-cli session export <id> ...` |
| `/session import <path> [--dry-run] [--title t]` | `andy-cli session import <path> ...` |
| `/session fork [id] [--at turn] [--title t]` | `andy-cli session fork <id> ...` |
| `/session rename [id] <title>` | `andy-cli session rename <id> <title>` |
| `/session stats [id] [--all]` | `andy-cli session stats [id] [--all]` |
| `/session list` | `andy-cli session list` (or `andy-cli sessions`) |

Interactively, `[id]` defaults to the session currently being recorded, and the live transcript
is flushed to disk before the subcommand runs, so `/session export` and `/session fork` always
see the turns just typed. In the noninteractive CLI there is no current session, so an id is
required for everything except `stats`, which falls back to the totals across all sessions.

## Export

`session export` writes a portable, versioned archive:

```json
{
  "format": "andy-session-archive",
  "schemaVersion": 1,
  "exportedUtc": "2026-07-25T18:15:30.0000000Z",
  "exportedBy": "andy-cli/2026.5.30.0",
  "checksum": { "algorithm": "sha256", "value": "<hex>" },
  "session": {
    "sessionId": "20260725-181530-3fa9",
    "title": "Refactor plan",
    "createdUtc": "...", "updatedUtc": "...",
    "provider": "openai", "model": "gpt-4o",
    "turnCount": 12,
    "firstUserMessage": "...",
    "lineage": { "parentSessionId": "...", "rootSessionId": "...", "forkedAtTurn": 4, "forkedUtc": "..." },
    "origin":  { "workspacePath": "/home/dev/proj", "platform": "linux" },
    "usage":   { "inputTokens": 0, "outputTokens": 0, "reasoningTokens": 0,
                 "cacheReadTokens": 0, "cacheWriteTokens": 0, "estimatedCostUsd": 0.0 },
    "transcript": { "...": "the engine's own versioned TranscriptSnapshot" }
  }
}
```

The checksum is the SHA-256 of the compact serialization of the `session` object, so it is
stable across pretty-printing of the outer document and detects tampering or truncation of the
payload. Writes are atomic (temp file plus move).

`--markdown` writes a human-readable transcript instead. `--tools` adds tool names, arguments,
and results; `--metadata` adds the provider/model, timestamps, lineage, origin, and usage
header. Tool payloads are truncated and always wrapped in a fence wide enough that the payload
cannot break out of its code block.

## Import

`session import` validates the archive completely before writing anything, so a rejected
archive leaves the session directory exactly as it was. Rejected cases:

- corrupt or truncated JSON
- a missing, unknown-algorithm, or mismatched checksum
- a file over the 64 MB ceiling (checked on the file before it is read)
- a `schemaVersion` newer than this build understands - it fails with a clear "upgrade
  andy-cli" message rather than partially installing a session it cannot interpret
- a `sessionId` that is not a safe file name (`../../evil`, `dir/evil`, ...)
- a transcript with no turns

When the archive's session id is free it is reused; when it is already taken a fresh
conflict-safe id is minted and the original id is kept in `lineage.importedFromSessionId`.
`--dry-run` prints exactly what a real import would do and writes nothing.

Import is inert: it parses JSON and writes one session file. It never runs a tool, never
replays a tool result, and never touches the workspace path recorded in the archive.

## Forking

`session fork [id] [--at N]` creates an independent session from a saved one.

- Without `--at`, the whole session is copied.
- With `--at N` (1-based user-turn numbering), the fork contains the history **strictly before
  turn N**, i.e. turns 1..N-1. That is the state the assistant was in just before the user sent
  turn N, which is what you want in order to take the conversation somewhere else from there.
  `--at 1` is rejected (it would produce an empty session); `--at` past the last turn is treated
  as a full fork.

The fork gets a brand-new session id, a default title (`Fork of <label> (before turn N)`), and
lineage recording its parent, the root of the fork chain, the boundary, and the fork time.
Because it is written as its own file, continuing either branch cannot mutate the other.

A partial fork covers only part of the source's traffic, so it starts with **unknown** usage
rather than inheriting the source totals. A full fork carries the totals over.

## Titles

`session rename [id] <title>` sets a human-readable title; passing an empty title clears it.
Titles appear in `/sessions`, survive ordinary per-turn saves, travel with archives, and are
what keeps imported and forked sessions discoverable among time-stamped ids.

## Usage and cost

The session envelope records aggregate `inputTokens`, `outputTokens`, `reasoningTokens`,
`cacheReadTokens`, `cacheWriteTokens`, and `estimatedCostUsd`.

Two rules matter:

1. **Unknown pricing is not zero cost.** `estimatedCostUsd` is omitted when the model is not in
   the static pricing table, and every consumer keeps that apart from a genuine zero (a locally
   hosted model really is free). Totals across sessions report a lower bound and say how many
   sessions had no pricing data.
2. **Cached and reasoning tokens are components, not extras.** Providers report them as subsets
   of the prompt/completion counts, so the headline total is `input + output` and the component
   counts are shown alongside rather than summed in.

Sessions saved before usage tracking existed simply have no `usage` field; they are reported as
"usage not recorded", again distinct from zero.

## Compatibility

Everything above is **additive within session schema version 1**. `title`, `lineage`, `origin`,
and `usage` are optional envelope fields, so:

- session files written before these features still load unchanged, and
- an ordinary per-turn save inherits whatever the existing file recorded, so a code path that
  knows nothing about titles or usage cannot erase them.

## Security and portability

- Archives are built from the already-redacted stored session and redacted again on the way
  out, so no API key, OAuth token, injected header, or other secret the session redactor removes
  can reach an archive or a Markdown export.
- Import executes nothing.
- An unknown future schema version fails before anything is installed.
- `origin.workspacePath` is metadata from the recording machine and is **informational only**.
  It is never used to open, create, or resolve a file: `SessionOrigin.ResolveLocalWorkspace()`
  returns a path only when it is rooted, free of traversal segments, and actually exists on this
  machine, which is false for a foreign platform's path by construction.

## Where the code lives

| File | Role |
| --- | --- |
| `src/Andy.Cli/Services/Sessions/SessionStore.cs` | Envelope read/write, atomic saves, rename, conflict-safe ids |
| `src/Andy.Cli/Services/Sessions/SessionMetadata.cs` | Lineage, origin, and save options |
| `src/Andy.Cli/Services/Sessions/SessionUsage.cs` | Token/cost aggregate with nullable cost |
| `src/Andy.Cli/Services/Sessions/SessionArchive.cs` | Archive format, checksum, validation |
| `src/Andy.Cli/Services/Sessions/SessionArchiveExporter.cs` | Archive and Markdown writers |
| `src/Andy.Cli/Services/Sessions/SessionArchiveImporter.cs` | Validation, dry run, install |
| `src/Andy.Cli/Services/Sessions/SessionMarkdownExporter.cs` | Markdown rendering and options |
| `src/Andy.Cli/Services/Sessions/SessionForker.cs` | Full and point-in-time forks |
| `src/Andy.Cli/Services/Sessions/SessionStatsFormatter.cs` | Stats aggregation and rendering |
| `src/Andy.Cli/Commands/SessionCommand.cs` | The `/session` and `andy-cli session` surface |
