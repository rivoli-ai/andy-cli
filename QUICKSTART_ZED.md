# Quick Start: Andy CLI with Zed

## 1. Build Andy CLI

```bash
cd /Users/samibengrine/Devel/rivoli-ai/andy-cli
dotnet build
```

## 2. Test it works

```bash
dotnet run --project src/Andy.Cli -- --acp
```

You should see:
```
info: Andy.Cli.ACP.AcpServerHost[0]
      ACP server initialized with 20 tools
```

Press Ctrl+C to stop.

## 3. Configure Zed

Edit `~/.config/zed/settings.json`:

```json
{
  "agent_servers": {
    "Andy CLI": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/Users/samibengrine/Devel/rivoli-ai/andy-cli/src/Andy.Cli",
        "--",
        "--acp"
      ],
      "env": {
        "OPENAI_API_KEY": "sk-..."
      }
    }
  }
}
```

**Replace**:
- `/Users/samibengrine/...` with your actual path
- `sk-...` with your OpenAI API key

## 4. Test in Zed

1. Restart Zed (completely quit and reopen)
2. Open Assistant Panel: `Cmd+?` (Mac) or `Ctrl+?` (Windows/Linux)
3. Try: "What tools do you have available?"

## Available Tools

Andy CLI exposes 20 tools:
- **File operations**: read, write, copy, move, delete, list directory
- **Git**: git diff
- **System**: process info, system info
- **Text**: format, replace, search
- **Web**: HTTP requests, JSON processing
- **Utilities**: datetime, encoding, todo management
- **Bash**: execute shell commands
- **Code**: code index search

## Troubleshooting

**"Connection failed"**
- Check the path in settings.json is absolute and correct
- Test the command manually in terminal
- Check Zed logs: View → Debug → Open Log

**"No tools showing"**
- Verify tools registered: `dotnet run --project src/Andy.Cli -- tools list`
- Check API key is set in settings

**For detailed help**: See `docs/ZED_INTEGRATION.md`
