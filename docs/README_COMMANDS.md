# Andy CLI - Model Management and Command Palette

## Features Added

### 1. Model Management Commands

The CLI now supports model management through slash commands:

- `/model list` or `/m list` - List all available AI models from different providers
- `/model switch <provider>` - Switch to a different provider (openrouter, openai, anthropic, cerebras, groq, gemini, ollama)
- `/model provider` - Show or change the active provider
- `/model refresh` - Refresh the available model list
- `/model detect` - Auto-detect available providers based on configured API keys
- `/model info` - Show detailed information about the current model
- `/tools list` - List available tools
- `/tools info` - Show detailed information about a tool
- `/permissions` (aliases: `perms`, `perm`) - View and edit tool permissions (allow/ask/deny rules, persisted to permission layer files) via an interactive permissions manager
- `/theme` (aliases: `themes`) - List, switch, or toggle transparency of the UI theme
- `/help` or `/?` - Show help information
- `/clear` - Clear the chat history

**Updated**: The list models command now shows:
- Models from all supported providers (OpenRouter, OpenAI, Anthropic, Cerebras, Groq, Gemini, Ollama)
- Current active model with an arrow indicator (->)
- API key status for each provider (available / missing)
- Which API keys are currently set in the environment
- Detailed model descriptions and capabilities

### 2. Command Palette (Ctrl+P / Cmd+P)

Press `Ctrl+P` (or `Cmd+P` on Mac) to open the command palette, which provides:

- Quick access to all commands
- Fuzzy search/filtering as you type
- Arrow keys to navigate
- Enter to execute
- Escape to close

Available commands in the palette:
- **List Models** - Show available AI models
- **Switch Model** - Change AI provider/model
- **Model Info** - Show current model details
- **Test Model** - Test current model
- **Clear Chat** - Clear conversation history
- **Toggle HUD** - Show/hide performance overlay
- **Help** - Show help information

## Usage Examples

### Using Slash Commands
```
/model list              # Show all available models
/model switch openai     # Switch to OpenAI provider
/model info              # Show current model details
/model test Hello world  # Test the current model
/clear                   # Clear chat history
/help                    # Show help
```

### Using Command Palette
1. Press `Ctrl+P` (or `Cmd+P` on Mac) to open the palette
2. Start typing to filter commands (e.g., "model" to see all model commands)
3. Use arrow keys to select a command
4. Press Enter to execute
5. Press Escape to close without executing

## Requirements

Set the appropriate environment variables for the providers you want to use:
- `OPENROUTER_API_KEY` for OpenRouter provider
- `OPENAI_API_KEY` for OpenAI provider (including the Codex model variants)
- `ANTHROPIC_API_KEY` for Anthropic provider
- `CEREBRAS_API_KEY` for Cerebras provider
- `GROQ_API_KEY` for Groq provider
- `GOOGLE_API_KEY` for Gemini provider
- `AZURE_OPENAI_API_KEY` (with `AZURE_OPENAI_ENDPOINT`) for Azure OpenAI provider
- `OLLAMA_API_BASE` for a local or remote Ollama instance

### Configured Providers

The following providers are defined in `src/Andy.Cli/appsettings.json`:
- **OpenRouter** - model `moonshotai/kimi-k3` (ApiBase `https://openrouter.ai/api/v1`); the primary configured provider
- **OpenAI** - `gpt-4o`, plus the Codex variants `gpt-5-codex`, `codex-mini-latest`, `gpt-5.1-codex-mini`, `gpt-5.1-codex-max`, `gpt-5.1-codex`, and `gpt-5.2-codex`
- **Anthropic** - `claude-3-5-haiku-20241022`
- **Cerebras** - `llama-3.3-70b`
- **Groq** - `llama-3.3-70b-versatile`
- **Google Gemini** - `gemini-2.0-flash-exp`
- **Ollama** - `llama3.2`

## Architecture

### Commands (`src/Andy.Cli/Commands/`)
- `ICommand.cs` - Base interface for all commands
- `ModelCommand.cs` - Implementation of model management commands

### Widgets (`src/Andy.Cli/Widgets/`)
- `CommandPalette.cs` - Command palette UI widget with search and selection
- `FeedView.cs` - Enhanced with `Clear()` method for clearing chat history

### Integration
The commands are integrated into `Program.cs` with:
- Slash command parsing for text input
- Keyboard shortcut handling for Ctrl+P
- Command execution through the palette
- Dynamic model switching with provider recreation

## Notes

- There is no hardcoded default provider; `DefaultProvider` is empty in `appsettings.json`, so the provider is auto-detected (use `/model detect`). OpenRouter (model `moonshotai/kimi-k3`) is the primary configured provider in `appsettings.json`.
- Model switching recreates the LLM client with the new provider
- The command palette supports fuzzy search across command names, descriptions, and aliases
- All UI updates are reflected immediately in the chat feed