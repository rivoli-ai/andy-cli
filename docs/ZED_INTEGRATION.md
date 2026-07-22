# Zed and ACP integration

Updated: 2026-07-21

Andy CLI can run as an Agent Client Protocol (ACP) server for Zed and other ACP
clients. ACP traffic uses standard input/output; diagnostics go to standard
error so they do not corrupt the protocol stream.

## Prerequisites

- Zed with external ACP-agent support.
- A published Andy CLI binary, or a .NET 8 SDK for source execution.
- Credentials for at least one configured remote provider, or a reachable local
  Ollama instance.

The [quickstart](QUICKSTART_ZED.md) is the shortest setup path.

## Recommended setup: published binary

Publish a self-contained executable for the host architecture:

```bash
dotnet publish src/Andy.Cli/Andy.Cli.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true \
  --output ./publish \
  -p:PublishSingleFile=true
```

Run two non-interactive checks before configuring the editor:

```bash
./publish/andy-cli --version
./publish/andy-cli tools list
```

Then add an `agent_servers` entry to Zed's `settings.json`:

```json
{
  "agent_servers": {
    "Andy CLI": {
      "command": "/absolute/path/to/publish/andy-cli",
      "args": ["--acp"]
    }
  }
}
```

Use the executable's absolute path. A published binary starts faster and avoids
build output being mixed into an editor-managed process.

## Source-checkout alternative

For local development, Zed can launch the project through the .NET SDK:

```json
{
  "agent_servers": {
    "Andy CLI (source)": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/andy-cli/src/Andy.Cli",
        "--",
        "--acp"
      ]
    }
  }
}
```

This is convenient while changing the server, but it is slower and can trigger
a build whenever Zed starts the process.

## Credentials and provider selection

Andy detects providers in this order when their credentials are available:
OpenRouter, OpenAI, Anthropic, Cerebras, Groq, Google Gemini, then local Ollama.
The primary credential variables are:

- `OPENROUTER_API_KEY`
- `OPENAI_API_KEY`
- `ANTHROPIC_API_KEY`
- `CEREBRAS_API_KEY`
- `GROQ_API_KEY`
- `GOOGLE_API_KEY`

Ollama uses `OLLAMA_API_BASE` when supplied and otherwise probes
`http://localhost:11434`. `ANDY_SKIP_OLLAMA=1` disables that probe.

Prefer launching Zed with the required environment or using a supported OS/IDE
secret-injection mechanism. Zed also accepts an `env` object in an agent entry,
but values stored there are plain configuration data: do not commit secrets or
paste production credentials into a shared settings file.

ACP mode advertises a grouped model picker for the providers available in the
server environment. Changing the selection rebuilds the agent for that ACP
session with the selected provider/model. The session's conversation context is
reset, while other sessions remain unchanged. Provider availability is derived
from configured credentials (or a reachable local provider).

## Current ACP behavior

For each new ACP session, Andy creates an independent `SimpleAgent` backed by
Andy.Engine and the live Andy Tools registry. It supports:

- New sessions.
- Loading a session retained by the same running server process.
- Embedded text and resource context supplied by the client.
- Cancellation propagated to the active agent operation.
- Provider/model identification on the first prompt.
- Per-session provider/model selection through the ACP `model` config option.
- Progress narration, tool-start notifications, actual tool results, and a final
  response through `session/update`.

Current limitations:

- Session state is bounded and in-memory; restarting the ACP process loses it.
- Loading a retained session restores the agent object but cannot replay a
  transcript to the client.
- Session list, fork, durable resume, and mode switching are not implemented.
- Image and audio prompt capabilities are not advertised.
- Intermediate narration and tool activity are incremental, but final model text
  is currently delivered as one completed chunk.
- Andy CLI is not in the public ACP registry, so it requires a custom agent entry.

The active ACP follow-ups are token/rich-content streaming (#204), persistent
session catalog/resume (#206), registry packaging (#214), and multimodal prompts
(#215). Model config options (#205) are implemented.

## Tools and permissions

`andy-cli tools list` currently reports 54 built-in tools: file/code, text,
system, git, HTTP/JSON, utility/productivity, six PDF, and 28 dataframe tools.
See the [feature comparison](CLI_AGENT_FEATURE_COMPARISON.md#built-in-andy-tools-catalog)
for the complete inventory.

ACP tool execution uses the same permission-aware Engine/Tools path as the CLI.
The permission surface is still alpha: use a disposable branch/worktree, review
changes, and do not expose critical or unbacked-up data.

## Troubleshooting

### Agent does not start

1. Confirm the configured command is absolute and executable.
2. Run `<command> --version` outside Zed.
3. Run `<command> --acp` in a terminal; it should remain running and wait for an
   ACP client. Stop it with `Ctrl+C`.
4. Inspect Zed's agent/server logs for stderr from Andy.

### Provider authentication fails

1. Confirm the Zed process receives the intended credential variable.
2. From a shell with the same environment, run `andy-cli model detect`.
3. If Ollama is unintentionally winning or slowing startup, set
   `ANDY_SKIP_OLLAMA=1`.

### Tools are missing

Run `andy-cli tools list` using the exact binary from the Zed configuration. A
source build and a published binary may reference different Andy.Tools package
versions.

### Protocol errors or immediate disconnects

- Do not wrap the ACP command in a script that writes banners or status text to
  stdout.
- Capture diagnostics from stderr, for example:

  ```bash
  ANDY_DEBUG=true /absolute/path/to/andy-cli --acp 2>andy-acp.log
  ```

- Do not test the server with ad-hoc JSON-RPC payloads copied from older ACP
  drafts. Use an ACP client or the repository's ACP tests so protocol-version and
  framing details match the installed Andy.Acp.Core package.

## Related documentation

- [Zed external agents](https://zed.dev/docs/ai/external-agents)
- [Agent Client Protocol](https://agentclientprotocol.com/)
- [Andy command reference](README_COMMANDS.md)
- [Rider CLI-agent comparison](CLI_AGENT_FEATURE_COMPARISON.md)
