# Changed-file LSP diagnostics

Added: 2026-07-25 (rivoli-ai/andy-cli#282, phases 1 and 2)

Andy can ask a language server what it thinks of a file immediately after a tool changed it, and
hand the resulting errors and warnings to both you and the model. This turns "the edit looked
plausible" into "the edit type-checks", without waiting for a later build.

The feature is **off until you configure a server**. Andy never downloads, installs, or bundles a
language server: it launches a command you already have.

## What happens on a file change

After a successful `write_file`, `edit_file`, or `replace_text`:

1. *(reserved for rivoli-ai/andy-cli#283)* a post-mutation formatter rewrites the file.
2. Andy resolves a configured server for the file's extension.
3. It finds the server's project root by walking up from the file to the nearest root marker,
   never above the workspace root.
4. It starts that server if it is not already running (once per server + root, deduplicated across
   concurrent changes).
5. It sends `textDocument/didOpen` (or `didChange`) plus `didSave` carrying **the file's current
   on-disk content**, read back after the tool ran.
6. It waits up to `diagnosticsTimeoutMs` for `textDocument/publishDiagnostics`.
7. Errors and warnings are attached to the tool result under `lsp_diagnostics` (so the model sees
   them) and printed under the tool call in the feed.

Step 5 is why the ordering in step 1 matters: diagnostics describe whatever is on disk when Andy
reads it, so a formatter must run *before* this, not after. The integration point is
`UiUpdatingToolExecutor.ReportLanguageServerDiagnosticsAsync`, called from the block commented
`POST-MUTATION PIPELINE`.

## Configuration

Create `.andy/lsp-servers.json` in the project root:

```json
{
  "servers": {
    "csharp": {
      "command": "csharp-ls",
      "args": [],
      "extensions": [".cs"],
      "rootMarkers": ["*.sln", "*.csproj"],
      "diagnosticsTimeoutMs": 8000,
      "startTimeoutMs": 30000
    }
  }
}
```

| Field | Meaning |
| --- | --- |
| `command` | Executable to launch. Must be on `PATH` or an absolute path. Required. |
| `args` | Arguments passed to the command. |
| `env` | Extra environment variables, merged over the inherited environment. |
| `extensions` | File extensions this server claims, with or without the leading dot. Required. |
| `rootMarkers` | Files or directories (globs allowed) that mark the project root. The nearest ancestor of the changed file that contains one wins. Defaults to the workspace root. |
| `languageId` | Language id sent in `didOpen`. Defaults to the server's key. |
| `initializationOptions` | Raw JSON passed straight through to `initialize`. |
| `enabled` | Set to `false` to keep a definition without using it. |
| `startTimeoutMs` | Deadline for the `initialize` handshake. Default 15000. |
| `diagnosticsTimeoutMs` | How long one file change may wait for diagnostics. Default 3000. |

The same shape is available in `appsettings.json` under `Lsp:Servers` (PascalCase keys). The
project file wins field-by-field over appsettings, so a project can override just the command.

> **Configuration seam.** rivoli-ai/andy-cli#280 introduces layered user/project configuration.
> `LspConfigurationLoader` is deliberately the only place that knows where definitions come from;
> when #280 lands, its body is replaced with a read of the layered store and
> `LspConfigurationLoadResult` stays as the contract.

### Working examples

These are real, installable servers. Pick the ones you use; nothing is enabled by default.

```json
{
  "servers": {
    "csharp": {
      "command": "csharp-ls",
      "extensions": [".cs"],
      "rootMarkers": ["*.sln", "*.csproj"],
      "startTimeoutMs": 30000,
      "diagnosticsTimeoutMs": 8000
    },
    "typescript": {
      "command": "typescript-language-server",
      "args": ["--stdio"],
      "extensions": [".ts", ".tsx", ".js", ".jsx"],
      "rootMarkers": ["tsconfig.json", "package.json"],
      "diagnosticsTimeoutMs": 5000
    },
    "python": {
      "command": "pyright-langserver",
      "args": ["--stdio"],
      "extensions": [".py"],
      "rootMarkers": ["pyproject.toml", "setup.py", "requirements.txt"]
    },
    "go": {
      "command": "gopls",
      "extensions": [".go"],
      "rootMarkers": ["go.mod"]
    },
    "rust": {
      "command": "rust-analyzer",
      "extensions": [".rs"],
      "rootMarkers": ["Cargo.toml"],
      "startTimeoutMs": 60000,
      "diagnosticsTimeoutMs": 10000
    }
  }
}
```

Install commands, for reference: `dotnet tool install --global csharp-ls`,
`npm i -g typescript-language-server typescript`, `npm i -g pyright`,
`go install golang.org/x/tools/gopls@latest`, `rustup component add rust-analyzer`.

Servers that index a whole project (rust-analyzer, gopls on a cold cache) can take a long time
before their first useful diagnostics. Raise `startTimeoutMs`/`diagnosticsTimeoutMs` for those
rather than expecting a 3-second answer.

## Commands

```
/lsp status          Configured servers, their state, root, uptime and failures
/lsp restart [id]    Stop servers (all, or one by id) and forget remembered failures
/lsp help            Usage
```

States: `idle` (configured, not started yet), `starting`, `running`, `failed` (never started),
`crashed` (started, then exited), `disabled`.

`/lsp status` prints the exact command line that was tried plus the last lines the server wrote to
stderr, which is almost always the whole diagnosis for a server that will not start.

`/lsp restart` also clears remembered failures, so after installing a missing binary you do not
have to restart Andy.

## Safety properties

These are contract, not implementation detail, and each is covered by a test.

- **Nothing can hang the agent loop.** Startup, the handshake, and the diagnostics wait all have
  hard deadlines. Server absence, launch failure, crash, malformed frames, and slow analysis all
  resolve into a bounded status rather than a stuck turn.
- **Nothing can crash the agent loop.** Every path through the LSP layer is exception-contained;
  the worst case is that a successful tool call returns without diagnostics.
- **One server per project root.** Concurrent file changes converge on a single startup, and a
  failure is remembered instead of retried on every write.
- **Bounded restarts.** A crashed server is restarted automatically at most twice; after that it
  stays down until `/lsp restart`.
- **No orphans.** Every server process is shut down (politely first, then killed) when the session
  ends, including one still mid-handshake.
- **Workspace containment.** A changed file outside the workspace root is never forwarded, and a
  server's project root is never discovered above the workspace root. Symlinks are resolved before
  the check, so a link inside the workspace cannot smuggle a path out. Both rules are lifted only
  by setting `"allowOutsideWorkspace": true` explicitly.

## Output bounds

Per changed file (`Andy.Cli.Lsp.LspLimits`):

- at most 20 diagnostics, errors first, then warnings, then the rest;
- at most 240 characters per message;
- at most 2000 characters of rendered diagnostics;
- files larger than 2 MB are not synchronized at all.

Anything dropped is reported rather than hidden. The structured payload gains `truncated`,
`reported_count`, `total_count`, `omitted_count`, and `truncation_reason`, and the feed prints
`... N more not shown (reason)`.

## Structure of the model-visible payload

Attached to the mutating tool's result as `lsp_diagnostics`:

```json
{
  "server": "csharp",
  "file": "/w/src/Thing.cs",
  "status": "received",
  "error_count": 1,
  "warning_count": 0,
  "diagnostics": [
    {
      "severity": "error",
      "line": 42,
      "column": 9,
      "message": "The name 'foo' does not exist in the current context",
      "code": "CS0103",
      "source": "csharp"
    }
  ]
}
```

Lines and columns are 1-based (LSP is 0-based on the wire). `status` is one of `received`,
`timed_out`, `server_unavailable`, `outside_workspace`, `skipped`; the non-`received` cases carry a
`detail` explaining what went wrong. A clean file attaches nothing at all.

## Testing

Integration tests drive the real client - real framing, real JSON-RPC, real document
synchronization - against `Andy.Cli.Tests.Lsp.FakeLanguageServer`, a deterministic in-repo server
that runs over an in-process stream pair. Its diagnostics are a pure function of the document text
(`ERROR` on a line produces an error, `WARN` a warning, `FLOOD` sixty errors), and it can be told
to hang on initialize, crash mid-sync, emit garbage, or never publish. No language server binary is
required to run the suite.

Two tests additionally launch real child processes (a missing binary, and `/bin/sh -c "sleep 120"`)
to prove that a launch failure is an owned exception and that disposal leaves no orphan. They are
skipped on Windows.

## Not implemented

Model-facing hover, definition, references, document symbols, and call hierarchy are the "Later"
section of issue #282 and are deliberately out of scope. The client speaks only the subset needed
for changed-file diagnostics.

## Related

- [Tool Execution Architecture](./tool-execution-architecture.md)
- [MCP configuration](./mcp-configuration.md) - the same stdio-subprocess shape for remote tools
