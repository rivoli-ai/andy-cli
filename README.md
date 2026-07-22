# Andy CLI

A .NET 8 coding agent with an interactive terminal UI, a strict headless
runtime, and an Agent Client Protocol (ACP) server for editor integrations.

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

- **Interactive TUI** - Terminal chat, live agent/tool progress, diffs, themes,
  prompt history, and expandable tool results
- **Multi-Provider Support** - Works with OpenRouter, OpenAI, Anthropic, Cerebras, Groq, Google Gemini, and Ollama
- **Smart Provider Detection** - Automatically selects the best available LLM provider
- **54 Built-in Tools** - File operations, code and text search, shell commands,
  code indexing, git diffs, HTTP/JSON, 28 dataframe tools, and six PDF tools
- **Permission Management** - Interactive permission prompts and layered
  allow/ask/deny rules through `/permissions`
- **UI Themes** - 34 built-in themes, persistence, and optional transparent
  backgrounds through `/theme`
- **Code Indexing** - Index a codebase for fast code-aware search
- **Headless Agent Runtime** - Non-interactive agent runtime with structured exit codes
- **ACP Server Mode** - Run as an Agent Client Protocol server for editor integrations
- **Observability** - Instrumentation and a performance HUD; crash logging for diagnostics
- **Reusable .NET Components** - Built on the Andy.Engine, Andy.Llm,
  Andy.Tools, Andy.Permissions, Andy.MCP, Andy.Acp.Core, and Andy.Tui packages

## Build and run

```bash
dotnet build
dotnet run --project src/Andy.Cli
```

Published, self-contained binaries are produced by the GitHub release workflow
for macOS, Linux, and Windows on x64 and ARM64. Verify downloaded artifacts
against the release checksums before running them.

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

1. **OpenRouter** - Requires `OPENROUTER_API_KEY` (default Kimi K3 setup)
2. **OpenAI** - Requires `OPENAI_API_KEY` (reliable fallback)
3. **Anthropic** - Requires `ANTHROPIC_API_KEY`
4. **Cerebras** - Requires `CEREBRAS_API_KEY` (fast inference)
5. **Groq** - Requires `GROQ_API_KEY`
6. **Google Gemini** - Requires `GOOGLE_API_KEY` (alias: `gemini`)
7. **Ollama** (local) - Detected if running on localhost:11434 (no API key required)

### Environment Variables

#### Required for Providers

- `OPENAI_API_KEY` - OpenAI API key
- `OPENROUTER_API_KEY` - OpenRouter API key
- `ANTHROPIC_API_KEY` - Anthropic (Claude) API key
- `CEREBRAS_API_KEY` - Cerebras API key
- `GROQ_API_KEY` - Groq API key
- `GOOGLE_API_KEY` - Google Gemini API key
- `OLLAMA_API_BASE` - Custom Ollama endpoint (default:
  `http://localhost:11434`)

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

# Use OpenRouter (default Kimi K3 setup)
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

- `Ctrl+P` (or `Cmd+P`) - Open command palette
- `Ctrl+]` - Toggle scroll mode (Feed vs Prompt History)
- `Ctrl+O` - Expand/collapse tool output detail
- `F2` - Toggle performance HUD
- `F3` - Toggle mouse capture (on by default for wheel scrolling; use
  Option/Shift+drag to select, or turn capture off for native selection)
- `Esc` or `Ctrl+D` - Open the exit confirmation (Esc first closes an open
  palette or permission manager)
- `Up/Down` - Move within the prompt or navigate prompt history in history mode
- `Page Up/Down` - Fast scroll

#### Slash Commands

- `/model list` - Show available models
- `/model switch <model>` - Switch to a different model (same provider)
- `/model switch <provider> <model>` - Switch provider and model together
- `/model provider <name>` - Switch to a different provider
- `/model refresh` - Refresh model lists from the provider API
- `/model info` - Show current model details
- `/model detect` - Show provider detection diagnostics
- `/tools list` - List available tools
- `/tools info <tool_name>` - Show details for a tool
- `/permissions` - View and edit tool permissions (aliases: `perms`, `perm`)
- `/theme` - List and switch the UI theme (alias: `themes`)
- `/theme transparent on|off` - Toggle a supported theme's transparent background
- `/clear` - Clear conversation history
- `/help` - Show help information

See [`docs/README_COMMANDS.md`](docs/README_COMMANDS.md) for the complete
command and keyboard reference.

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

#### ACP behavior

ACP sessions report the resolved provider/model on the first prompt and stream
model progress narration, tool starts, and real tool completion results through
`session/update`. Editors such as Zed can therefore render activity while the
agent is working instead of showing only the final answer.

Each session also advertises a grouped `model` configuration picker containing
the providers available in the server environment. Changing it rebuilds that
session with the selected provider/model and resets only that session's
conversation context.

Sessions are retained only in the running ACP process. Andy currently supports
new sessions, in-process session loading, embedded text/resource context, and
cancellation, but not session listing, forking, durable resume, mode switching,
or image/audio prompts. See [`docs/ZED_INTEGRATION.md`](docs/ZED_INTEGRATION.md)
and the [Rider agent comparison](docs/CLI_AGENT_FEATURE_COMPARISON.md).

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

### Documentation

- [`docs/README.md`](docs/README.md) - Documentation index
- [`docs/README_COMMANDS.md`](docs/README_COMMANDS.md) - Commands and shortcuts
- [`docs/headless-runtime.md`](docs/headless-runtime.md) - Headless config and execution contract
- [`docs/ZED_INTEGRATION.md`](docs/ZED_INTEGRATION.md) - ACP editor integration
- [`docs/CLI_AGENT_FEATURE_COMPARISON.md`](docs/CLI_AGENT_FEATURE_COMPARISON.md) - Rider CLI-agent comparison

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

### SDK and dependency policy

The build is pinned to a .NET 8 SDK via `global.json`, restore is locked to
committed `packages.lock.json` files, and the known-good Andy engine/TUI/package
versions are recorded in [`dependency-manifest.json`](dependency-manifest.json).
See [`docs/SDK_AND_DEPENDENCIES.md`](docs/SDK_AND_DEPENDENCIES.md) for the SDK
band policy, how to update it, the CI SDK check (`scripts/assert-sdk-version.sh`),
and the engine/TUI source-revision compatibility workflow.

For a cross-repository change, run
`ANDY_SKIP_OLLAMA=1 scripts/check-source-compat.sh` with sibling Andy.Engine and
Andy.Tui checkouts. The helper uses an isolated project-reference overlay and
leaves committed package references and lock files unchanged. The same check is
available as the manual `Source compatibility` GitHub Actions workflow.

Dependency status (2026-07-21): the CLI's direct NuGet references and recursive
Andy package graph were refreshed to the latest verified stable or prerelease
versions for the .NET 8 target. The package lock files and dependency manifest
record the resulting known-good graph.

## License

See [`LICENSE`](LICENSE) for details.
