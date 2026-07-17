# andy-cli
Command line AI code assistant powered by .NET 8

> **ALPHA RELEASE WARNING**
>
> This software is in ALPHA stage. **NO GUARANTEES** are made about its functionality, stability, or safety.
>
> **CRITICAL WARNINGS:**
> - This tool performs **DESTRUCTIVE OPERATIONS** on files and directories
> - Permission management is **NOT FULLY TESTED** and may have security vulnerabilities
> - **DO NOT USE** in production environments
> - **DO NOT USE** on systems with critical or irreplaceable data
> - **DO NOT USE** on systems without complete, verified backups
> - The authors assume **NO RESPONSIBILITY** for data loss, system damage, or security breaches
>
> **USE AT YOUR OWN RISK**

## Features

- **Interactive TUI** - Modern terminal interface with real-time streaming responses
- **Multi-Provider Support** - Works with OpenRouter, OpenAI, Anthropic, Cerebras, Groq, Google Gemini, and Ollama
- **Smart Provider Detection** - Automatically selects the best available LLM provider
- **Tool Execution** - File operations, code and text search, shell commands (execute_command), code indexing (code_index), git_diff, dataframe tools, http_request, and MCP tools
- **Permission Management** - Interactive permission manager (/permissions) and permission layers for controlling tool access
- **UI Themes** - Switchable terminal UI themes (/theme)
- **Code Indexing** - Index a codebase for fast code-aware search
- **Headless Agent Runtime** - Non-interactive agent runtime with structured exit codes
- **ACP Server Mode** - Run as an Agent Client Protocol server for editor integrations
- **Observability** - Instrumentation and a performance HUD; crash logging for diagnostics
- **Performance Optimized** - Efficient streaming and rendering with Andy.Tui framework

## Installation

```bash
dotnet build
dotnet run --project src/Andy.Cli
```

## Configuration

### Automatic Provider Detection

By default no provider is pinned (the `DefaultProvider` setting in
`src/Andy.Cli/appsettings.json` is empty), so Andy CLI automatically detects
and selects the best available LLM provider based on your environment
variables. Configured providers include OpenRouter, OpenAI, Anthropic,
Cerebras, Groq, Google Gemini, and Ollama. The provider set, endpoints,
required environment variables, defaults, and detection priorities are all
defined in a single registry (`src/Andy.Cli/Services/ProviderRegistry.cs`)
that provider detection and the `/model` command both read from. When several
providers are configured, detection prefers them in this order:

1. **OpenRouter** - Requires `OPENROUTER_API_KEY` (default Mimo-v2.5 setup)
2. **OpenAI** - Requires `OPENAI_API_KEY` (reliable fallback)
3. **Anthropic** - Requires `ANTHROPIC_API_KEY`
4. **Cerebras** - Requires `CEREBRAS_API_KEY` (fast inference)
5. **Groq** - Requires `GROQ_API_KEY`
6. **Google Gemini** - Requires `GOOGLE_API_KEY` (alias: `gemini`)
7. **Ollama** (local) - Detected if running on localhost:11434 (no API key required)

### Environment Variables

#### Required for Providers

See Andy.Llm library documentation.

- `OPENAI_API_KEY` - OpenAI API key
- `OPENROUTER_API_KEY` - OpenRouter API key
- `ANTHROPIC_API_KEY` - Anthropic (Claude) API key
- `CEREBRAS_API_KEY` - Cerebras API key
- `GROQ_API_KEY` - Groq API key
- `GOOGLE_API_KEY` - Google Gemini API key
- `OLLAMA_API_BASE` - Custom Ollama endpoint (default: http://localhost:11434)

#### Provider Control

- `ANDY_SKIP_OLLAMA=1` - Skip Ollama even if it's running locally
- `ANDY_OLLAMA_AUTO_DETECT=false` - Disable automatic Ollama detection (only use if OLLAMA_API_BASE is set)

#### Optional Configuration

- `OPENAI_API_BASE` - Custom OpenAI API endpoint
- `ANDY_DEBUG=true` - Enable debug logging
- `ANDY_STRICT_ERRORS=1` - Enable strict error handling

### Examples

```bash
# Use OpenAI (if OPENAI_API_KEY is set)
ANDY_SKIP_OLLAMA=1 dotnet run --project src/Andy.Cli

# Force a specific provider
dotnet run --project src/Andy.Cli -- model provider openai

# Check provider detection
dotnet run --project src/Andy.Cli -- model detect

# Use OpenRouter (default Mimo-v2.5 setup)
export OPENROUTER_API_KEY="your-key"
dotnet run --project src/Andy.Cli
```

## Commands

### Interactive Mode (TUI)

Run without arguments to start the interactive terminal interface:

```bash
dotnet run --project src/Andy.Cli
```

#### Keyboard Shortcuts

- `Ctrl+P` - Open command palette
- `Ctrl+]` - Toggle scroll mode (Feed vs Prompt History)
- `Ctrl+O` - Expand/collapse tool output detail
- `F2` - Toggle performance HUD
- `F3` - Toggle mouse capture (off by default so native text selection works; on enables mouse-wheel scroll)
- `ESC` - Exit application
- `Up/Down` - Scroll through chat history
- `Page Up/Down` - Fast scroll

#### Slash Commands

- `/model list` - Show available models
- `/model switch <model>` - Switch to a different model (same provider)
- `/model provider <name>` - Switch to a different provider
- `/model refresh` - Refresh model lists from the provider API
- `/model info` - Show current model details
- `/model detect` - Show provider detection diagnostics
- `/tools list` - List available tools
- `/tools info <tool_name>` - Show details for a tool
- `/permissions` - View and edit tool permissions (aliases: `perms`, `perm`)
- `/theme` - List and switch the UI theme (alias: `themes`)
- `/clear` - Clear conversation history
- `/help` - Show help information

### Command Line Mode

```bash
# Model management
dotnet run --project src/Andy.Cli -- model list
dotnet run --project src/Andy.Cli -- model provider openai
dotnet run --project src/Andy.Cli -- model detect

# Tool management
dotnet run --project src/Andy.Cli -- tools list
dotnet run --project src/Andy.Cli -- tools info <tool_name>
```

### Version

```bash
# Print the version and exit (--version, -v, or version)
dotnet run --project src/Andy.Cli -- --version
```

### Headless Agent Runtime

Run the agent non-interactively from a config file. The headless runtime
returns structured exit codes (see the `HeadlessExitCode` enum: `Success`,
`ConfigError`, `AgentFailure`, `Timeout`, and others) so it can be scripted
in CI and automation. See `docs/headless-runtime.md` for full details.

```bash
dotnet run --project src/Andy.Cli -- run --headless --config <path>
```

### ACP Server Mode

Run as an Agent Client Protocol (ACP) server for editor integrations:

```bash
dotnet run --project src/Andy.Cli -- --acp
```

## Development

### Architecture

The interactive assistant is implemented by `SimpleAssistantService`
(`src/Andy.Cli/Services/SimpleAssistantService.cs`), which wraps `SimpleAgent`
from the Andy.Engine NuGet package (`SimpleAgent` lives in the package, not in
this repository). The application is built on a set of Andy.* NuGet packages:

- **Andy.Engine** - Agent loop and tool execution (provides `SimpleAgent`)
- **Andy.Llm** - LLM provider abstractions and implementations
- **Andy.Tools** + **Andy.Tools.Data** - Tool framework, including dataframe tools
- **Andy.Tui** - High-performance terminal UI framework
- **Andy.Model** - Shared models and abstractions
- **Andy.Permissions** - Permission engine for tool access control
- **Andy.MCP** - Model Context Protocol (MCP) tool support
- **Andy.Acp.Core** - Agent Client Protocol (ACP) server
- **Andy.CodeIndex.Infrastructure** - Code indexing

### Project Structure

- `src/Andy.Cli/` - Main CLI application
- `src/Andy.Cli/Services/` - Core services including provider detection and assistants
- `src/Andy.Cli/Commands/` - Command implementations (model, tools, etc.)
- `src/Andy.Cli/Tools/` - Additional tool implementations
- `src/Andy.Cli/Widgets/` - TUI components and views
- `tests/Andy.Cli.Tests/` - Unit and integration tests

### Testing

```bash
# Run all tests (skip Ollama detection for CI environments)
ANDY_SKIP_OLLAMA=1 dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Generate coverage report
reportgenerator -reports:"./TestResults/*/coverage.cobertura.xml" -targetdir:"./TestResults/CoverageReport" -reporttypes:Html
```

### Building

```bash
# Debug build
dotnet build

# Release build
dotnet build -c Release

# Clean build artifacts
dotnet clean
```

## License

See LICENSE file for details.
