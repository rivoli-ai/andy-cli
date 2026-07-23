# Interactive MCP configuration

Updated: 2026-07-23

Andy CLI can connect the interactive TUI to Model Context Protocol (MCP)
servers over local stdio or Streamable HTTP. Tools discovered during startup
are registered beside Andy's built-in tools and are available to the model,
`/tools list`, and `/tools info`.

Headless MCP bindings use the separate versioned configuration documented in
[Headless runtime](headless-runtime.md).

## Configuration sources

Interactive mode merges two sources:

1. `Mcp:Servers` in the packaged `appsettings.json`
2. `<working-directory>/.andy/mcp-servers.json`

The project file wins when both sources define the same server name. Server
names are compared case-insensitively. The project file is read once at
startup; editing it does not change a running session.

### Project file

Create `.andy/mcp-servers.json` in the directory where Andy CLI starts:

```json
{
  "servers": {
    "filesystem": {
      "transport": "stdio",
      "command": "npx",
      "args": [
        "-y",
        "@modelcontextprotocol/server-filesystem",
        "."
      ],
      "workingDirectory": ".",
      "env": {
        "LOG_LEVEL": "warn"
      }
    },
    "internal-api": {
      "transport": "http",
      "url": "https://mcp.example.test/rpc",
      "headers": {
        "Authorization": "Bearer ${INTERNAL_MCP_TOKEN}"
      }
    }
  }
}
```

### appsettings.json

The equivalent application configuration is:

```json
{
  "Mcp": {
    "Servers": {
      "filesystem": {
        "Transport": "stdio",
        "Command": "npx",
        "Args": [
          "-y",
          "@modelcontextprotocol/server-filesystem",
          "."
        ],
        "WorkingDirectory": "."
      }
    }
  }
}
```

## Server fields

| Field | Applies to | Description |
| --- | --- | --- |
| `transport` | all | Required. `stdio` or `http`. `sse` and `streamable-http` are accepted HTTP aliases. |
| `enabled` | all | Optional; defaults to `true`. Disabled servers appear in `/mcp status` but are not started. |
| `command` | stdio | Required executable or command path. Andy starts it directly without a shell. |
| `args` | stdio | Optional ordered argument array. |
| `workingDirectory` | stdio | Optional child-process working directory. |
| `env` | stdio | Optional environment variables merged into the child process environment. |
| `url` | HTTP | Required absolute `http` or `https` MCP endpoint. |
| `headers` | HTTP | Optional headers included on each MCP request. |

Every string field supports `${VARIABLE_NAME}` interpolation. A server with an
unset referenced variable is rejected before a process or connection is
opened. Put secrets in the environment, not in a committed JSON file. Error
and status output reports field and variable names but does not print expanded
secret values.

## Startup and tool names

Interactive mode initializes enabled servers before creating the model agent.
Each server has a ten-second connection/discovery timeout. A failed server is
recorded as failed without preventing other MCP servers or the TUI from
starting.

Discovered tools receive collision-resistant IDs in this form:

```text
mcp_<normalized-server-name>_<normalized-remote-tool-name>
```

For example, `read-note` from `local-files` becomes
`mcp_local_files_read_note`. Its description begins with
`[MCP: local-files]`, so `/tools list` shows the source. Calls are forwarded to
the original remote tool name, and protocol or transport failures return
ordinary failed tool results.

## Commands

| Command | Behavior |
| --- | --- |
| `/mcp list` | List configured servers, state, transport, and tool count. |
| `/mcp status` | Include registered tool IDs and sanitized failure details. |
| `/mcp help` | Show the supported MCP commands. |
| `/tools list` | List MCP tools alongside built-in tools. |

Adding, removing, or reloading servers from a running TUI, gateway discovery,
health polling, MCP resources, and MCP prompts are not implemented yet. Restart
Andy CLI after changing configuration.

## Troubleshooting

- Use `/mcp status` first. It distinguishes disabled, connected, and failed
  servers.
- Run the configured stdio command manually if the server exits during its MCP
  initialization handshake.
- Confirm the HTTP URL is the MCP RPC endpoint, not a product landing page.
- Confirm every `${VARIABLE_NAME}` is exported in the environment that launches
  Andy CLI.
- Use `ANDY_DEBUG=true` for detailed local logs. Debug logs can contain server
  paths and protocol diagnostics; treat them as sensitive.

## Completion summary

On 2026-07-23, Phase 1 interactive MCP support added merged configuration,
environment interpolation, stdio and HTTP transports, graceful per-server
startup failure, shared tool-registry integration, server-qualified tool
metadata, deterministic cleanup, and `/mcp list|status`.
