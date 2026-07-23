# Andy CLI command reference

Updated: 2026-07-23

Examples use the published executable name, `andy-cli`. From a source checkout,
replace it with `dotnet run --project src/Andy.Cli --`.

## Execution modes

| Purpose | Command |
| --- | --- |
| Interactive terminal UI | `andy-cli` |
| Print version | `andy-cli --version`, `andy-cli -v`, or `andy-cli version` |
| Model command | `andy-cli model <subcommand>` |
| Tool command | `andy-cli tools <subcommand>` |
| Headless run | `andy-cli run --headless --config <path>` |
| ACP server | `andy-cli --acp` |

`andy-cli run` without both `--headless` and `--config` is rejected; there is no
second interactive `run` mode. See [Headless runtime](headless-runtime.md) for the
versioned config and exit-code contract.

## Interactive commands

### Models and providers

| Command | Behavior |
| --- | --- |
| `/model list` | List configured providers and available models. |
| `/model switch <model>` | Change model, retaining the current provider when possible. |
| `/model switch <provider> <model>` | Change provider and model together. |
| `/model provider <provider>` | Change provider and restore its remembered/default model. |
| `/model refresh` | Clear cached model lists and query provider APIs again. |
| `/model info` | Show the active provider, model, and endpoint. |
| `/model detect` | Explain provider detection and credential availability. |
| `/model test [prompt]` | Send a direct test prompt to the active model. |

Aliases include `/m`, `list`/`ls`, `switch`/`sw`, and `provider`/`p`. Provider
IDs are `openrouter`, `openai`, `anthropic`, `cerebras`, `groq`, `google`, and
`ollama`; `gemini` is an alias for `google`.

### Tools

| Command | Behavior |
| --- | --- |
| `/tools list [category]` | List the live built-in catalog, optionally filtered by category. |
| `/tools info <tool_id>` | Show a tool's schema, parameters, and permissions. |
| `/tools execute <tool_id> [name=value ...]` | Execute a tool through the normal permission path. |

Use IDs from `/tools list`; do not infer an ID from the display name. The current
catalog contains 54 tools, including 28 dataframe and six PDF tools. The catalog
is package-version dependent, and interactive MCP tools are added at startup, so
the live command is authoritative.

### MCP servers

| Command | Behavior |
| --- | --- |
| `/mcp list` | List configured MCP servers, transport, connection state, and tool count. |
| `/mcp status` | Include registered tool IDs and sanitized failure details. |
| `/mcp help` | Show MCP command usage. |

Interactive MCP servers come from `Mcp:Servers` in `appsettings.json` and the
project-local `.andy/mcp-servers.json`. See
[Interactive MCP configuration](mcp-configuration.md) for schemas, environment
interpolation, supported transports, and current management limitations.

### Permissions

| Command | Behavior |
| --- | --- |
| `/permissions` | Open the interactive permission manager. |
| `/permissions list` | List effective layered rules. |
| `/permissions <allow/ask/deny> <tool[(spec)]> [--scope <user/project/local>]` | Persist a rule at the selected scope. |
| `/permissions path` | Show permission rule-file locations. |

`perms` and `perm` are aliases. Mutating file tools and `execute_command`
normally require consent unless a higher-precedence rule allows them.

### Themes and general commands

| Command | Behavior |
| --- | --- |
| `/theme` | List 34 built-in themes and show the active theme. |
| `/theme <name>` | Switch theme and persist the choice. |
| `/theme transparent <on/off>` | Toggle transparency when the selected theme supports it. |
| `/clear` | Clear the current feed/conversation history. |
| `/help` or `/?` | Show in-application help. |
| `/exit`, `/quit`, or `/bye` | Open the exit confirmation. The same words work without `/`. |

## Keyboard shortcuts

| Key | Behavior |
| --- | --- |
| `Ctrl+P` / `Cmd+P` | Open the command palette. |
| `Ctrl+]` | Toggle Feed and Prompt History modes. |
| `Ctrl+O` | Expand or collapse tool parameters and result previews; it does not affect execution. |
| `F2` | Toggle the performance HUD. |
| `F3` | Toggle mouse capture. Capture starts on when raw terminal input is available, enabling wheel scroll. |
| `Page Up` / `Page Down` | Scroll the feed by one page. |
| `Up` / `Down` | Move within multi-line input or navigate history in Prompt History mode. |
| `Ctrl+A` / `Ctrl+E` | Move to the start/end of the current line. |
| `Home` / `End` | Move to the line edge; with Control, move to the entire prompt edge. |
| `Ctrl+K` / `Ctrl+U` | Delete to the end/start of the current line. |
| `Esc` | Close the active palette/permission manager, otherwise open exit confirmation. |
| `Ctrl+D` | Open exit confirmation. |

With mouse capture on, use Option+drag on macOS or the terminal's Shift-modified
selection to select text. Press `F3` for ordinary native click-drag selection.

## Provider environment variables

| Provider | Credential | Optional endpoint override |
| --- | --- | --- |
| OpenRouter | `OPENROUTER_API_KEY` | `OPENROUTER_API_BASE` |
| OpenAI | `OPENAI_API_KEY` | `OPENAI_API_BASE` |
| Anthropic | `ANTHROPIC_API_KEY` | - |
| Cerebras | `CEREBRAS_API_KEY` | - |
| Groq | `GROQ_API_KEY` | - |
| Google Gemini | `GOOGLE_API_KEY` | - |
| Ollama | none | `OLLAMA_API_BASE` |

`ANDY_SKIP_OLLAMA=1` skips local probing. `ANDY_OLLAMA_AUTO_DETECT=false`
disables probing unless `OLLAMA_API_BASE` is explicitly set. Model defaults are
configuration, not a stable public API; inspect `src/Andy.Cli/appsettings.json`
or use `/model info` and `/model list` rather than copying a model inventory from
documentation.
