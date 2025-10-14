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
- **Multi-Provider Support** - Works with OpenAI, Cerebras, Azure OpenAI, and Ollama
- **Smart Provider Detection** - Automatically selects the best available LLM provider
- **Tool Execution** - File operations, code search, bash commands, and more
- **Context-Aware** - Maintains conversation history with intelligent context management
- **Performance Optimized** - Efficient streaming and rendering with Andy.Tui framework

## Installation

```bash
dotnet build
dotnet run --project src/Andy.Cli
```

## Configuration

### Automatic Provider Detection

Andy CLI automatically detects and selects the best available LLM provider based on your environment variables. The detection follows this priority order:

1. **OpenAI** - Requires `OPENAI_API_KEY` (highest priority for reliability)
2. **Cerebras** - Requires `CEREBRAS_API_KEY` (fast inference)
3. **Ollama** (local) - Detected if running on localhost:11434 (no API key required)
4. **Azure OpenAI** - Requires `AZURE_OPENAI_API_KEY` and `AZURE_OPENAI_ENDPOINT`

Note: Anthropic and Google Gemini providers are recognized but require Andy.Llm package updates for full support.

### Environment Variables

#### Required for Providers

See Andy.Llm library documentation.

- `OPENAI_API_KEY` - OpenAI API key
- `AZURE_OPENAI_API_KEY` - Azure OpenAI API key
- `AZURE_OPENAI_ENDPOINT` - Azure OpenAI endpoint URL
- `CEREBRAS_API_KEY` - Cerebras API key
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

# Force specific provider
dotnet run --project src/Andy.Cli -- model switch openai

# Check provider detection
dotnet run --project src/Andy.Cli -- model detect

# Use Azure OpenAI
export AZURE_OPENAI_API_KEY="your-key"
export AZURE_OPENAI_ENDPOINT="https://your-instance.openai.azure.com"
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
- `F2` - Toggle performance HUD
- `ESC` - Exit application
- `Up/Down` - Scroll through chat history
- `Page Up/Down` - Fast scroll

#### Slash Commands

- `/model list` - Show available models
- `/model switch <provider>` - Change provider
- `/model info` - Show current model details
- `/model detect` - Show provider detection diagnostics
- `/tools list` - List available tools
- `/clear` - Clear conversation history
- `/help` - Show help information

### Command Line Mode

```bash
# Model management
dotnet run --project src/Andy.Cli -- model list
dotnet run --project src/Andy.Cli -- model switch openai
dotnet run --project src/Andy.Cli -- model detect

# Tool management
dotnet run --project src/Andy.Cli -- tools list
dotnet run --project src/Andy.Cli -- tools info <tool_name>
```

## Development

### Architecture

Andy CLI is built on a modular architecture using:
- **Andy.Engine** - Core AI agent engine with tool execution
- **Andy.Llm** - LLM provider abstractions and implementations
- **Andy.Tools** - Extensible tool framework for file operations, code search, etc.
- **Andy.Tui** - High-performance terminal UI framework
- **Andy.Model** - Shared models and abstractions

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
