# Migrating to andy.jsonc

Updated: 2026-07-25

Andy configuration used to be spread across the packaged `appsettings.json`,
environment variables, `.andy/mcp-servers.json`, and a few small memory files
under `~/.andy`. [`andy.jsonc`](configuration.md) replaces the parts of that
which are genuinely user configuration.

**Nothing you have today stops working.** Every previous source is still read, at
a defined place in the precedence chain. This guide is about moving settings to
where they are now easiest to inspect and share, not about a required migration.

Start by looking at what you have:

```console
$ andy-cli config show --effective --sources
```

Every line ends with the file, line and column that produced it, so you can see
exactly which of the old sources is still supplying each value.

## Where each old source now sits

| Old source | Status | Layer |
| --- | --- | --- |
| Packaged `appsettings.json` (`Llm`, `Mcp` sections) | Still read | Packaged defaults (lowest) |
| `~/.andy/theme-memory.json` | Still read and still written by `/theme` | Applies when nothing declares `ui.theme` |
| `~/.andy/model-memory.json` | Still read and written by `/model` | Unchanged; remembers the last model per provider |
| `.andy/mcp-servers.json` | Still read | Above `mcp.servers` in `andy.jsonc` |
| `~/.andy/permissions.json` and friends | Unchanged, separate security format | Not merged; locations shown by `config show` |
| `ANDY_*` and provider environment variables | Still read | Environment layer |
| `--auto`, `--yolo`, `--debug`, `--verbose`, `--quiet` | Still read | CLI layer (highest) |
| `run --headless --config <path>` | Unchanged, separate contract | Does not read `andy.jsonc` |

## appsettings.json

The packaged `appsettings.json` is folded into the packaged-defaults layer. Its
PascalCase keys are matched case-insensitively against the schema, `${VAR}`
placeholders are resolved like `{env:VAR}`, and `""` is treated as "not set".

Before, in `appsettings.json`:

```json
{
  "Llm": {
    "DefaultProvider": "openrouter",
    "Providers": {
      "openrouter": {
        "Provider": "openrouter",
        "ApiBase": "https://openrouter.ai/api/v1",
        "ApiKey": "${OPENROUTER_API_KEY}",
        "Model": "moonshotai/kimi-k3",
        "Enabled": true
      }
    }
  }
}
```

After, in `~/.andy/andy.jsonc`:

```jsonc
{
  "version": 1,
  "llm": {
    "defaultProvider": "openrouter",
    "providers": {
      "openrouter": {
        "apiBase": "https://openrouter.ai/api/v1",
        "apiKey": "{env:OPENROUTER_API_KEY}",
        "model": "moonshotai/kimi-k3"
      }
    }
  }
}
```

Because it is a higher layer, you only need to write the fields you want to
change. `{ "llm": { "providers": { "openrouter": { "model": "..." } } } }` is a
complete and valid file.

`appsettings.json` remains the right place for the `Logging` section consumed by
`Microsoft.Extensions.Logging` at host level; `logging.level` and
`logging.console` in `andy.jsonc` control Andy's own logger.

## MCP servers

`.andy/mcp-servers.json` still works and still wins over `andy.jsonc`, so you can
move servers one at a time.

Before, in `.andy/mcp-servers.json`:

```json
{
  "servers": {
    "filesystem": {
      "transport": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "."],
      "workingDirectory": ".",
      "env": { "LOG_LEVEL": "warn" }
    }
  }
}
```

After, in `<workspace>/andy.jsonc`:

```jsonc
{
  "version": 1,
  "mcp": {
    "servers": {
      "filesystem": {
        "transport": "stdio",
        "command": "npx",
        "args": ["-y", "@modelcontextprotocol/server-filesystem", "."],
        "workingDirectory": ".",
        "env": { "LOG_LEVEL": "warn" }
      }
    }
  }
}
```

The field names are identical. Two differences worth knowing:

- `workingDirectory` now resolves against the file that declared it rather than
  the process working directory. `"."` next to `andy.jsonc` at the repository
  root means the repository root, whichever directory you launched from.
- `args` is replaced, not concatenated, when a higher layer declares it. That is
  what lets a project shorten a list the user file set.

Delete the server from `.andy/mcp-servers.json` once it is in `andy.jsonc`;
leaving it there means the old file keeps winning.

## Theme

`/theme <name>` still writes `~/.andy/theme-memory.json`, and that choice still
applies. `ui.theme` in a config file, `ANDY_THEME`, or `--theme` now override it,
so a project can pin a theme and a flag can override everything for one run.

Before:

```console
$ export ANDY_THEME=nord
```

After, in `~/.andy/andy.jsonc`:

```jsonc
{ "version": 1, "ui": { "theme": "nord", "transparentBackground": false } }
```

## Environment variables

Every variable in the table below keeps working. Move it into a file when you
want it committed, reviewable, or per-project.

| Variable | Equivalent |
| --- | --- |
| `ANDY_THEME=nord` | `"ui": { "theme": "nord" }` |
| `ANDY_DIFF_STYLE=split` | `"ui": { "diffStyle": "split" }` |
| `ANDY_MAX_TURNS=500` | `"session": { "maxTurns": 500 }` |
| `ANDY_AUTO_APPROVE=1` | `"permissions": { "mode": "auto" }` |
| `ANDY_DEBUG=true` | `"logging": { "console": true, "level": "information" }` |
| `OPENAI_API_BASE=https://...` | `"llm": { "providers": { "openai": { "apiBase": "https://..." } } }` |
| `OPENAI_MODEL=gpt-4o` | `"llm": { "providers": { "openai": { "model": "gpt-4o" } } }` |

**Leave API keys in the environment.** Reference them with `{env:NAME}` rather
than pasting the value into a file that might be committed. Andy never prints a
substituted value back out.

Note the direction of precedence: the environment is a HIGHER layer than any
file. An exported `ANDY_THEME` will keep overriding your new `ui.theme` until you
unset it.

## Sessions

Saved sessions still live in `~/.andy/sessions`. To move them, set:

```jsonc
{ "version": 1, "session": { "directory": "~/state/andy-sessions" } }
```

A relative path resolves against the declaring file, so
`{ "session": { "directory": "sessions" } }` in `<workspace>/andy.jsonc` keeps a
project's sessions inside that project.

## Permissions

Permission rules are **not** migrated and will not be. `permissions.json`,
`permissions.local.json` and the user rule file remain a dedicated security
format with their own merge rules, edited through `/permissions` and
`andy-cli permissions`. Keeping a security decision in a general-purpose,
user-editable settings file is exactly the mistake this split avoids.

What did change: their locations are now discoverable. `andy-cli config show
--effective` lists them under "Resolved locations".

Only the prompting mode is configurable here:

```jsonc
{ "version": 1, "permissions": { "mode": "auto" } }
```

which is the file equivalent of `--auto` / `--yolo` / `ANDY_AUTO_APPROVE`.

## Headless runs

`andy-cli run --headless --config <path>` is unchanged and deliberately does not
read `andy.jsonc`. A containerised run must reproduce from the single file it was
handed; letting a stray `~/.andy/andy.jsonc` on the host change its behaviour
would defeat that. See [Headless runtime](headless-runtime.md).

## Checklist

1. `andy-cli config show --effective --sources` to see where everything comes from now.
2. Create `~/.andy/andy.jsonc` with your personal defaults.
3. Create `<workspace>/andy.jsonc` for anything the team should share, and commit it.
4. Move MCP servers out of `.andy/mcp-servers.json`, deleting each one as you go.
5. Unset any environment variable you replaced with a file setting, since the
   environment is the higher layer.
6. `andy-cli config validate` to confirm every file parses and matches the schema.
