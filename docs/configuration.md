# Layered configuration (andy.jsonc)

Updated: 2026-07-25

Andy CLI reads one configuration file format, `andy.jsonc`, from a fixed set of
locations, merges them in a documented order, validates every source against a
versioned JSON Schema, and can show you the result with the origin of every value.

Related: [Interactive MCP configuration](mcp-configuration.md) for the dedicated
`.andy/mcp-servers.json` file, [Headless runtime](headless-runtime.md) for the
separate `headless-config.v1` contract, and [Migrating to andy.jsonc](configuration-migration.md).

## File locations

| Scope | Path | Notes |
| --- | --- | --- |
| User | `~/.andy/andy.jsonc` | Personal defaults for every project. |
| Project | `<workspace>/andy.jsonc` | Committed project settings. |
| Project | `<workspace>/.andy/andy.jsonc` | Project settings kept out of the repository root. Read last, so it wins over the root file. |

`<workspace>` is the directory Andy CLI was started in. Missing files are simply
skipped. If the workspace happens to be `~/.andy`, the file is loaded once, not
twice.

## Precedence

```text
packaged defaults  <  user  <  project  <  environment  <  CLI arguments
```

The highest layer that declares a value wins. Layers are merged per field, not
per file, so a project file that sets one key keeps everything else the user file
provided.

`andy-cli config show --effective --sources` prints the winner and its origin for
every value.

## The file

`andy.jsonc` is JSON with comments and trailing commas.

```jsonc
{
  // Optional: gives editors schema completion.
  "$schema": "https://rivoli-ai.com/schemas/andy-cli/andy-config.v1.json",
  "version": 1,

  "llm": {
    "defaultProvider": "openrouter",
    "defaultModel": "moonshotai/kimi-k3",
    "providers": {
      "openrouter": {
        "apiBase": "https://openrouter.ai/api/v1",
        "apiKey": "{env:OPENROUTER_API_KEY}",
        "model": "moonshotai/kimi-k3"
      },
      // A named alias for an existing provider implementation.
      "work-proxy": {
        "provider": "openai",
        "apiBase": "https://llm.corp.example/v1",
        "apiKey": "{env:CORP_LLM_TOKEN}",
        "model": "gpt-4o"
      }
    }
  },

  "mcp": {
    "servers": {
      "filesystem": {
        "transport": "stdio",
        "command": "npx",
        "args": ["-y", "@modelcontextprotocol/server-filesystem", "."],
        "workingDirectory": ".",
        "env": { "LOG_LEVEL": "warn" }
      },
      "internal-api": {
        "transport": "http",
        "url": "https://mcp.example.test/rpc",
        "headers": { "Authorization": "Bearer {env:INTERNAL_MCP_TOKEN}" }
      }
    }
  },

  "ui": {
    "theme": "dark",
    "transparentBackground": false,
    "diffStyle": "auto"
  },

  "session": {
    "directory": "~/.andy/sessions",
    "maxTurns": 300
  },

  "permissions": {
    // "auto" is equivalent to --auto / --yolo / ANDY_AUTO_APPROVE.
    "mode": "ask"
  },

  "logging": {
    "level": "warning",
    "console": false
  }
}
```

Every section and field is defined in
[`schemas/andy-config.v1.json`](../schemas/andy-config.v1.json), which the binary
also embeds. `andy-cli config schema` prints the exact schema the running build
validates against.

### Sections

| Section | Fields | Applies to |
| --- | --- | --- |
| `version` | `1` | The schema version this file is written for. |
| `llm` | `defaultProvider`, `defaultModel`, `providers.<name>.{provider,apiBase,apiKey,model,enabled,headers}` | Provider and model selection. |
| `mcp` | `servers.<name>.{transport,enabled,url,command,args,workingDirectory,env,headers}` | Interactive MCP servers. |
| `ui` | `theme`, `transparentBackground`, `diffStyle` | Terminal UI. |
| `session` | `directory`, `maxTurns` | Saved sessions and the agent-loop cap. |
| `permissions` | `mode` (`ask` \| `auto`) | Whether tool approvals prompt. |
| `logging` | `level`, `console` | Diagnostic logging. |

## Merge semantics

Merging is per field. There is no rule that applies to a whole file.

| Kind | Rule | Why |
| --- | --- | --- |
| Objects | Merged recursively | A higher layer setting `llm.providers.openai.model` keeps the lower layer's `apiBase`. |
| Keyed maps (`llm.providers`, `mcp.servers`, `env`, `headers`) | Merged entry by entry | Adding a server must not delete the others. |
| Arrays (for example `mcp.servers.*.args`) | **Replaced, never concatenated** | Concatenation makes a list impossible to shorten and silently doubles repeated arguments. |
| Scalars | Replaced | Highest layer wins. |
| Explicit `null` | Unsets the key | Restores the schema default, so a project can undo a user setting. |

## Environment variables

The environment layer translates the variables Andy already honoured. Their names
are unchanged.

| Variable | Maps to |
| --- | --- |
| `ANDY_THEME` | `ui.theme` |
| `ANDY_DIFF_STYLE` (`unified` \| `stacked` \| `split` \| `side-by-side` \| `auto`) | `ui.diffStyle` |
| `ANDY_MAX_TURNS` | `session.maxTurns` |
| `ANDY_AUTO_APPROVE` (any non-empty value) | `permissions.mode = "auto"` |
| `ANDY_DEBUG=true` | `logging.console = true`, `logging.level = "information"` |
| `OPENROUTER_API_KEY`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `CEREBRAS_API_KEY`, `GROQ_API_KEY`, `GOOGLE_API_KEY` | `llm.providers.<id>.apiKey` |
| `OPENROUTER_API_BASE`, `OPENAI_API_BASE`, `OLLAMA_API_BASE` | `llm.providers.<id>.apiBase` |
| `<PROVIDER>_MODEL` (for example `OPENAI_MODEL`) | `llm.providers.<id>.model` |

Variables outside this table (`ANDY_INSTRUMENTATION*`, `ANDY_PERMISSION_MODE`,
`ANDY_PERMISSIONS_FILE`, `ANDY_TOKEN`, ...) are read where they always were and
are not part of the merged configuration.

## Command-line arguments

The highest layer. Existing flags keep their meaning.

| Argument | Maps to |
| --- | --- |
| `--auto`, `--yolo` | `permissions.mode = "auto"` |
| `--debug` | `logging.console = true`, `logging.level = "debug"` |
| `--verbose` | `logging.level = "debug"` |
| `--quiet` | `logging.level = "none"` |
| `--theme <name>` | `ui.theme` |
| `--diff-style <auto\|unified\|split>` | `ui.diffStyle` |
| `--provider <id>` | `llm.defaultProvider` |
| `--model <id>` | `llm.defaultModel` |
| `--max-turns <n>` | `session.maxTurns` |

`--flag=value` is accepted as well as `--flag value`.

## Environment substitution

Any string may reference an environment variable:

```jsonc
{ "llm": { "providers": { "openai": { "apiKey": "{env:OPENAI_API_KEY}" } } } }
```

`${NAME}` is accepted as an alias so existing `appsettings.json` and
`.andy/mcp-servers.json` values migrate without an edit.

- A variable that is not set is an **error** in a user or project file. The
  diagnostic names the file, line, column, key path and variable.
- The same reference in the packaged defaults is only a warning: the defaults
  declare a key for every supported provider, and no machine has all of them.
- **Substituted values are never printed.** `config show` replaces them with
  `<redacted>` wherever they appear, including inside a longer string such as a
  URL with an embedded token.

## Relative paths

`session.directory` and `mcp.servers.*.workingDirectory` may be relative. They
resolve against **the directory of the file that declared them**, not the process
working directory, so `"directory": "sessions"` in
`<workspace>/.andy/andy.jsonc` always means `<workspace>/.andy/sessions`. Values
coming from the environment or the command line resolve against the workspace.
A leading `~` expands to the home directory.

## Commands

```console
$ andy-cli config validate
$ andy-cli config show --effective --sources
$ andy-cli config show --json --sources
$ andy-cli config sources
$ andy-cli config schema
```

`config validate` exits non-zero when any layer has an error. `config show`
prints every effective value, the resolved locations Andy computed (including the
permission rule files), and, with `--sources`, the layer, file, line and column
each value came from.

### Secret redaction

`config show` never prints:

- a value at a key that names a credential (`apiKey`, `token`, `secret`,
  `password`, `authorization`, ...),
- any value inside a `headers` map, whichever header it is,
- any value produced by `{env:NAME}` substitution, wherever it ends up.

Diagnostic messages are scrubbed of resolved secrets before they are printed too.

## Diagnostics

Every problem carries a stable code, a source, a line, a column and a key path.

| Code | Meaning |
| --- | --- |
| `ANDYCFG001` | The file is not valid JSONC, or its root is not an object. |
| `ANDYCFG002` | Unknown key. The message suggests the closest legal key. |
| `ANDYCFG003` | The value does not satisfy the schema (wrong type, outside an enum, out of range). |
| `ANDYCFG004` | `{env:NAME}` refers to a variable that is not set. |
| `ANDYCFG005` | A path field does not denote a usable path. |
| `ANDYCFG006` | The file exists but could not be read. |
| `ANDYCFG007` | The merged configuration could not be bound to typed options. |

Example:

```text
error ANDYCFG002 project:/work/repo/andy.jsonc:12:3 [ui.themee]: unknown key 'themee'. Did you mean 'theme'?
```

Keys are case sensitive. `"UI"` is an unknown key, not another spelling of `ui`.

## What is deliberately not in here

- **Permission rules.** `permissions.json`, `permissions.local.json` and the user
  rule file keep their own security format and are not merged into this
  configuration. Only `permissions.mode` is configurable here. Their resolved
  locations are printed by `config show --effective` so they are still easy to
  find.
- **The headless run config.** `andy-cli run --headless --config <path>` is a
  single self-contained contract (`headless-config.v1`) precisely so that a
  containerised run reproduces from one file. It does not read `andy.jsonc`.

## Extending the schema

Adding a section is four edits and no new machinery, because merge, provenance,
substitution, redaction and path resolution are all schema-driven:

1. Add the block to `schemas/andy-config.v1.json` with
   `"additionalProperties": false`.
2. Add the matching property to `AndyConfiguration` in
   `src/Andy.Cli/Configuration/AndyConfiguration.cs`.
3. If the section holds filesystem paths, list them in
   `ConfigPathResolver.PathPatterns`.
4. Document it above and pin its precedence in
   `tests/Andy.Cli.Tests/Configuration/ConfigPrecedenceTests.cs`.

A field that nothing reads must not be added. An inactive setting is worse than a
missing one, for the reasons recorded in
[ADR 0002](adr/0002-headless-v1-inactive-fields.md).
