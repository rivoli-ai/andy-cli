# Quickstart: Andy CLI with Zed

Updated: 2026-07-21

Andy CLI exposes an Agent Client Protocol (ACP) server over standard input and
output. Zed starts the process for each agent connection.

## 1. Publish the executable

```bash
dotnet publish src/Andy.Cli/Andy.Cli.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true \
  --output ./publish \
  -p:PublishSingleFile=true
```

Change the runtime for the target platform (`osx-x64`, `linux-x64`,
`linux-arm64`, `win-x64`, or `win-arm64`). Confirm the result:

```bash
./publish/andy-cli --version
./publish/andy-cli tools list
```

## 2. Configure Zed

Add this entry to Zed's `settings.json`, replacing the command with the absolute
path to the published executable:

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

Ensure the Zed process receives one supported provider credential, such as
`OPENROUTER_API_KEY`, `OPENAI_API_KEY`, or `ANTHROPIC_API_KEY`. Do not commit a
credential in `settings.json`. If the GUI application does not inherit shell
environment variables, use an OS/IDE credential-injection mechanism or a local,
ignored settings file.

## 3. Connect and test

Restart the agent server, select **Andy CLI** in Zed's agent panel, and ask it to
list or read files in the open project. On the first prompt Andy reports the
resolved provider/model and then sends progress, tool-start, tool-result, and
final response updates.

Andy currently exposes 54 built-in tools. The live list from `tools list` is
authoritative because package upgrades can add or remove tools.

For lifecycle limitations, source-build alternatives, and troubleshooting, see
[Zed and ACP integration](ZED_INTEGRATION.md).
