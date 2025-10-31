# Andy CLI User Guide

## Table of Contents
- [Getting Started](#getting-started)
- [Installation](#installation)
- [Configuration](#configuration)
- [Basic Usage](#basic-usage)
- [Advanced Features](#advanced-features)
- [Commands Reference](#commands-reference)
- [Keyboard Shortcuts](#keyboard-shortcuts)
- [Troubleshooting](#troubleshooting)
- [Best Practices](#best-practices)

## Getting Started

### What is Andy CLI?

Andy CLI is an AI-powered command-line assistant that provides an interactive terminal interface for AI-powered code assistance and automation. It combines the power of modern Large Language Models (LLMs) with practical tool execution capabilities.

### Key Benefits

- **AI-Powered Assistance**: Get help with coding, file operations, and system tasks
- **Multi-Provider Support**: Works with OpenAI, Cerebras, Anthropic, Google Gemini, Ollama, and Azure OpenAI
- **Rich Tool Ecosystem**: 20+ built-in tools for common development tasks
- **Interactive Terminal UI**: Modern, responsive terminal interface
- **Real-Time Monitoring**: Comprehensive debugging and performance tracking

## Installation

### Prerequisites

- **.NET 8.0 SDK** or later
- **Terminal with ANSI color support**
- **At least one LLM provider API key**

### Step 1: Clone the Repository

```bash
git clone <repository-url>
cd andy-cli
```

### Step 2: Build the Solution

```bash
dotnet build
```

### Step 3: Set Up API Keys

Choose your preferred LLM provider and set the appropriate environment variable:

```bash
# OpenAI (recommended)
export OPENAI_API_KEY="your-openai-api-key"

# Cerebras (fast inference)
export CEREBRAS_API_KEY="your-cerebras-api-key"

# Anthropic (excellent reasoning)
export ANTHROPIC_API_KEY="your-anthropic-api-key"

# Google Gemini
export GOOGLE_API_KEY="your-google-api-key"

# Ollama (local, no API key needed)
# Just ensure Ollama is running on localhost:11434

# Azure OpenAI
export AZURE_OPENAI_API_KEY="your-azure-key"
export AZURE_OPENAI_ENDPOINT="https://your-instance.openai.azure.com"
```

### Step 4: Run the Application

```bash
dotnet run --project src/Andy.Cli
```

## Configuration

### Environment Variables

#### Required API Keys
- `OPENAI_API_KEY` - OpenAI API key
- `CEREBRAS_API_KEY` - Cerebras API key
- `ANTHROPIC_API_KEY` - Anthropic API key
- `GOOGLE_API_KEY` - Google API key
- `AZURE_OPENAI_API_KEY` - Azure OpenAI API key
- `AZURE_OPENAI_ENDPOINT` - Azure OpenAI endpoint URL

#### Optional Configuration
- `OPENAI_API_BASE` - Custom OpenAI API endpoint
- `OLLAMA_API_BASE` - Custom Ollama endpoint (default: http://localhost:11434)
- `ANDY_DEBUG=true` - Enable debug logging
- `ANDY_STRICT_ERRORS=1` - Enable strict error handling
- `ANDY_SKIP_OLLAMA=1` - Skip Ollama even if running locally

### Configuration File

Create or modify `src/Andy.Cli/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "Providers": {
    "OpenAI": {
      "ApiKey": "your-api-key",
      "BaseUrl": "https://api.openai.com/v1",
      "DefaultModel": "gpt-4o-mini",
      "Priority": 10
    }
  }
}
```

## Basic Usage

### Starting the Application

```bash
# Interactive mode (recommended)
dotnet run --project src/Andy.Cli

# Command line mode
dotnet run --project src/Andy.Cli -- model list
```

### First Interaction

Once the application starts, you'll see the terminal interface. Try asking a simple question:

```
What files are in the current directory?
```

The AI will use the `list_directory` tool to show you the files and provide a helpful summary.

### Understanding the Interface

The terminal interface consists of:

- **Header**: Shows application name, version, git info, and current directory
- **Feed Area**: Displays conversation history with syntax highlighting
- **Prompt Line**: Where you type your questions and commands
- **Status Bar**: Shows current status and token usage
- **Key Hints**: Displays available keyboard shortcuts

### Basic Commands

#### Slash Commands (in TUI)

```
/model list          # Show available models
/model switch openai # Switch to OpenAI provider
/model info          # Show current model details
/tools list          # List available tools
/tools info read_file # Show details about a specific tool
/clear               # Clear conversation history
/help                # Show help information
/exit                # Exit the application
```

#### Command Line Mode

```bash
# Model management
dotnet run --project src/Andy.Cli -- model list
dotnet run --project src/Andy.Cli -- model switch openai
dotnet run --project src/Andy.Cli -- model detect

# Tool management
dotnet run --project src/Andy.Cli -- tools list
dotnet run --project src/Andy.Cli -- tools info read_file
```

## Advanced Features

### Multi-Provider Support

Andy CLI automatically detects and selects the best available provider:

1. **OpenAI** (highest priority) - Most reliable, full tool support
2. **Anthropic** - Excellent reasoning, full tool support
3. **Cerebras** - Fast inference, limited to 4 essential tools
4. **Google Gemini** - Google's offering, full tool support
5. **Ollama** - Local deployment, full tool support
6. **Azure OpenAI** - Enterprise option, full tool support

### Tool Ecosystem

Andy CLI includes 20+ built-in tools organized by category:

#### File System Tools
- `read_file` - Read file contents
- `write_file` - Write content to files
- `list_directory` - List directory contents
- `copy_file` - Copy files
- `delete_file` - Delete files
- `move_file` - Move/rename files

#### Text Processing Tools
- `search_text` - Search within files
- `replace_text` - Find and replace text
- `format_text` - Format text content

#### System Tools
- `bash_command` - Execute shell commands
- `system_info` - Get system information
- `process_info` - Process information

#### Development Tools
- `code_index` - Index and search code
- `git_diff` - Show git differences
- `create_directory` - Create directories

#### Web & JSON Tools
- `http_request` - Make HTTP requests
- `json_processor` - Process JSON data

#### Utility Tools
- `datetime_tool` - Date/time operations
- `encoding_tool` - Text encoding detection
- `todo_management` - Task management

### Real-Time Monitoring

Access the instrumentation dashboard at `http://localhost:5555` to see:

- Real-time conversation flow
- Tool execution tracking
- Performance metrics
- Error monitoring
- System prompt visibility

### Advanced Context Management

The system maintains sophisticated conversation context:

- **Multi-iteration conversations**: Up to 12 iterations for complex tasks
- **Tool result integration**: Seamless integration of tool outputs
- **Context preservation**: Maintains conversation history across sessions
- **Smart truncation**: Prevents context overflow with intelligent limits

## Commands Reference

### Model Commands

#### `/model list` or `/model ls`
Shows all available models and providers with their status.

```
Available Models and Providers
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

▶ OPENAI [https://api.openai.com] (current)
  Models (3):
    ▸ gpt-4o-mini ← active
    • gpt-4o
    • gpt-3.5-turbo

  CEREBRAS [https://api.cerebras.ai]
  ⚠ No API key configured

  OLLAMA [http://localhost:11434]
  Models: Ollama service not running or unreachable
```

#### `/model switch <provider>` or `/model switch <provider> <model>`
Switch to a different provider or model.

```
# Switch to OpenAI provider
/model switch openai

# Switch to specific model
/model switch openai gpt-4o

# Auto-detect provider for model
/model switch gpt-4o-mini
```

#### `/model info`
Show detailed information about the current model.

#### `/model test [prompt]`
Test the current model with a simple prompt.

```
/model test Hello, how are you?
```

#### `/model detect`
Show provider detection diagnostics.

### Tool Commands

#### `/tools list [category]`
List available tools, optionally filtered by category.

```
/tools list
/tools list filesystem
/tools list text
```

#### `/tools info <tool_name>`
Show detailed information about a specific tool.

```
/tools info read_file
/tools info bash_command
```

#### `/tools execute <tool_name> [params]`
Execute a tool with specific parameters.

```
/tools execute read_file file_path=README.md
/tools execute list_directory directory_path=src
```

### General Commands

#### `/clear`
Clear the conversation history and reset the context.

#### `/help` or `/?`
Show comprehensive help information.

#### `/exit`, `/quit`, `/bye`
Exit the application (shows confirmation dialog).

## Keyboard Shortcuts

### Navigation
- `Up/Down` - Scroll through chat history
- `Page Up/Page Down` - Fast scroll
- `Ctrl+]` - Toggle scroll mode (Feed ↔ Prompt History)

### Interface
- `Ctrl+P` - Open command palette
- `F2` - Toggle performance HUD
- `ESC` - Exit application (shows confirmation)

### Text Editing (in Prompt)
- `Ctrl+A/E` - Jump to start/end of current line
- `Home/End` - Start/end of line (Ctrl: whole text)
- `Ctrl+K` - Delete from cursor to end of line
- `Ctrl+U` - Delete from start of line to cursor

### Scroll Modes

#### Feed Mode (Default)
- Blue indicator on left margin
- `Page Up/Page Down` scrolls conversation
- Shows scroll position indicator

#### Prompt History Mode
- Orange indicator on left margin
- `Up/Down` navigates previous messages
- Shows message counter (e.g., 5/12)

## Troubleshooting

### Common Issues

#### "No API key configured"
**Problem**: No LLM provider is available.

**Solution**:
1. Set at least one API key environment variable
2. Check provider detection: `/model detect`
3. Verify API key is valid and has sufficient credits

#### "Tool execution failed"
**Problem**: Tools are not working properly.

**Solution**:
1. Check tool permissions and file access
2. Verify tool parameters are correct
3. Check debug logs: `ANDY_DEBUG=true`

#### "Provider not responding"
**Problem**: LLM provider is not responding.

**Solution**:
1. Check internet connectivity
2. Verify API endpoint is correct
3. Check API key validity and rate limits
4. Try switching to a different provider

#### "Context overflow"
**Problem**: Conversation context is too large.

**Solution**:
1. Use `/clear` to reset context
2. Break down complex requests into smaller parts
3. Check output limiting settings

### Debug Mode

Enable debug mode for detailed logging:

```bash
ANDY_DEBUG=true dotnet run --project src/Andy.Cli
```

Debug information includes:
- Request/response details
- Tool execution traces
- Context state changes
- Parameter validation results

### Log Files

Response logs are saved to:
```
~/.andy/logs/llm/YYYY-MM-DD/response_HH-mm-ss_fff.txt
```

### Performance Issues

If the application is slow:

1. **Check Provider**: Some providers are faster than others
2. **Reduce Context**: Use `/clear` to reset context
3. **Limit Tools**: Some providers have tool limitations
4. **Monitor Resources**: Check the performance HUD (`F2`)

## Best Practices

### Effective Usage

1. **Be Specific**: Provide clear, specific requests
2. **Use Tools**: Let the AI use tools for complex tasks
3. **Monitor Context**: Keep conversations focused
4. **Clear Regularly**: Use `/clear` for new topics

### Security Considerations

⚠️ **Important**: This is alpha software with destructive capabilities.

1. **Backup Data**: Always backup important files
2. **Test First**: Test commands in safe environments
3. **Review Actions**: Review tool executions before confirming
4. **Use Permissions**: Configure appropriate tool permissions

### Performance Tips

1. **Choose Provider Wisely**: OpenAI for reliability, Cerebras for speed
2. **Limit Context**: Keep conversations focused
3. **Use Appropriate Tools**: Choose the right tool for the task
4. **Monitor Usage**: Watch token usage and performance metrics

### Development Workflow

1. **Start Simple**: Begin with basic questions
2. **Build Complexity**: Gradually increase task complexity
3. **Use Tools**: Leverage the tool ecosystem
4. **Iterate**: Use multi-turn conversations for complex tasks

---

*This user guide provides comprehensive instructions for using Andy CLI effectively. For technical details, see the Architecture Documentation.*
