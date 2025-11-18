# Zed Editor Integration Guide

This guide shows how to use Andy.CLI as an ACP (Agent Client Protocol) server with Zed editor.

## Prerequisites

1. **Zed Editor** installed - Download from [zed.dev](https://zed.dev/)
2. **.NET 8.0 SDK** or later
3. **Andy.CLI** built with ACP support
4. **API Keys** for your preferred LLM provider (OpenAI, Anthropic, Cerebras, etc.)

## Step 1: Build Andy.CLI

Build the project in Release mode for best performance:

```bash
cd /Users/samibengrine/Devel/rivoli-ai/andy-cli
dotnet build -c Release
```

Verify the build succeeded with no errors. This creates a pre-compiled DLL at:
`src/Andy.Cli/bin/Release/net8.0/andy-cli.dll`

## Step 2: Test ACP Server Manually

Before integrating with Zed, verify the ACP server works:

```bash
# Test that ACP mode starts (it will wait for JSON-RPC messages on stdin)
dotnet run --project src/Andy.Cli -- --acp

# You should see logs on stderr like:
# info: Andy.Cli.ACP.AcpServerHost[0]
#       Starting ACP server: Andy.CLI v1.0.0
# info: Andy.Cli.ACP.AcpServerHost[0]
#       ACP server initialized with X tools
```

Press `Ctrl+C` to stop.

### Test with a JSON-RPC Message

Create a test file `test-init.json`:

```json
{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"0.1.0","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
```

Test the server:

```bash
cat test-init.json | dotnet run --project src/Andy.Cli -- --acp
```

You should see a JSON-RPC response with server capabilities.

## Step 3: Configure Zed

### Find Zed Settings Location

- **macOS/Linux**: `~/.config/zed/settings.json`
- **Windows**: `%APPDATA%\Zed\settings.json`

### Add ACP Configuration

Open your Zed settings and add the **recommended** configuration using the pre-built DLL:

```json
{
  "agent_servers": {
    "Andy CLI": {
      "command": "dotnet",
      "args": [
        "/Users/samibengrine/Devel/rivoli-ai/andy-cli/src/Andy.Cli/bin/Release/net8.0/andy-cli.dll",
        "--acp"
      ],
      "env": {
        "OPENAI_API_KEY": "your-openai-api-key-here",
        "ANTHROPIC_API_KEY": "your-anthropic-api-key-here"
      }
    }
  }
}
```

**IMPORTANT**:
- Replace the DLL path with your actual absolute path to the built DLL
- Add your API keys in the `env` section
- Using the pre-built DLL avoids compilation delays when Zed starts the server

**Alternative** (slower startup):
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
        "OPENAI_API_KEY": "your-openai-api-key-here"
      }
    }
  }
}
```
Note: Using `dotnet run` compiles the project on each start, which may cause delays.

### Alternative: Use Published Binary

For better performance, publish Andy.CLI first:

```bash
cd /Users/samibengrine/Devel/rivoli-ai/andy-cli
dotnet publish src/Andy.Cli -c Release -o ./publish
```

Then in Zed settings:

```json
{
  "agent_servers": {
    "Andy CLI": {
      "command": "/Users/samibengrine/Devel/rivoli-ai/andy-cli/publish/andy-cli",
      "args": ["--acp"],
      "env": {
        "OPENAI_API_KEY": "your-openai-api-key-here"
      }
    }
  }
}
```

## Step 4: Test in Zed

1. **Restart Zed** completely (quit and reopen)

2. **Open Assistant Panel**
   - macOS: `Cmd+?`
   - Windows/Linux: `Ctrl+?`

3. **Verify Connection**
   - Look for "andy-cli" in the assistant provider list
   - Check for connection status (should show connected)

4. **Test Basic Interaction**

   Try a simple prompt:
   ```
   What tools do you have available?
   ```

   The assistant should list all Andy.CLI tools.

5. **Test Tool Execution**

   Try using a tool:
   ```
   Can you list the files in the current directory?
   ```

   The assistant should use the file listing tool.

## Troubleshooting

### Connection Issues

**Problem**: Zed shows "Connection failed" or doesn't connect

**Solutions**:
1. Check Zed's log output (View → Debug → Open Log)
2. Verify the command path is correct and absolute
3. Test the command manually in terminal:
   ```bash
   dotnet run --project /absolute/path/to/andy-cli/src/Andy.Cli -- --acp
   ```

### No Tools Showing

**Problem**: Assistant connects but doesn't show available tools

**Solutions**:
1. Check stderr output when running manually
2. Verify tools are registered:
   ```bash
   dotnet run --project src/Andy.Cli -- tools list
   ```

### JSON-RPC Errors

**Problem**: "Invalid JSON-RPC message" errors

**Solutions**:
1. Ensure no console output goes to stdout (only stderr)
2. Check that all logging uses stderr:
   ```bash
   ANDY_DEBUG=true dotnet run --project src/Andy.Cli -- --acp 2>debug.log
   ```

### API Key Issues

**Problem**: "API key not found" or authentication errors

**Solutions**:
1. Verify API keys are set in Zed settings `env` section
2. Check which provider Andy.CLI is configured to use:
   ```bash
   dotnet run --project src/Andy.Cli -- model info
   ```
3. Set the correct provider before starting:
   ```bash
   dotnet run --project src/Andy.Cli -- model switch openai
   ```

## Debugging Tips

### Enable Verbose Logging

Add to Zed settings:

```json
{
  "agent_servers": {
    "Andy CLI": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/andy-cli/src/Andy.Cli", "--", "--acp"],
      "env": {
        "ANDY_DEBUG": "true",
        "ANDY_LOG_LEVEL": "Debug",
        "OPENAI_API_KEY": "your-key"
      }
    }
  }
}
```

### Monitor Server Output

Run Andy.CLI manually and check stderr:

```bash
dotnet run --project src/Andy.Cli -- --acp 2>&1 | tee andy-cli.log
```

Then send test messages via stdin.

### Check Available Tools

Test the tools/list method:

```bash
echo '{"jsonrpc":"2.0","method":"tools/list","id":2,"params":{}}' | \
  dotnet run --project src/Andy.Cli -- --acp
```

Expected response:
```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "tools": [
      {"name": "bash", "description": "...", "inputSchema": {...}},
      {"name": "file_read", "description": "...", "inputSchema": {...}},
      ...
    ]
  }
}
```

## Expected Behavior

### Successful Integration

When working correctly:

1. ✅ Zed Assistant Panel opens without errors
2. ✅ "andy-cli" appears as connected provider
3. ✅ Assistant responds to prompts
4. ✅ Tools are listed and can be executed
5. ✅ File operations work within workspace
6. ✅ No stdout pollution (only JSON-RPC messages)

### Performance

- **Startup time**: < 3 seconds
- **Tool execution**: Varies by tool, typically < 1 second
- **Response latency**: Depends on LLM provider

## Example Workflow

Here's a complete example of using Andy.CLI in Zed:

1. **Open a project in Zed**
   ```bash
   cd ~/my-project
   zed .
   ```

2. **Open Assistant** (`Cmd+?`)

3. **Ask about the project**
   ```
   What files are in this project? Give me an overview.
   ```

4. **Request a code change**
   ```
   Can you add error handling to the main.ts file?
   ```

5. **Run tests**
   ```
   Run the test suite and tell me if anything fails.
   ```

The assistant will use Andy.CLI tools to:
- List files
- Read file contents
- Edit files
- Execute bash commands (npm test, etc.)
- Report results

## Next Steps

- Read the ACP protocol spec: [Agent Client Protocol](https://github.com/zed-industries/agent-client-protocol)
- Customize tools in Andy.CLI
- Configure model preferences
- Set up workspace-specific settings

## Getting Help

If you encounter issues:

1. Check the [troubleshooting section](#troubleshooting) above
2. Review Zed logs: View → Debug → Open Log
3. Test Andy.CLI in standalone mode first
4. Check stderr output for detailed error messages
