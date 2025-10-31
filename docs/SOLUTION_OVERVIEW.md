# Andy CLI Solution Overview

## Table of Contents
- [Introduction](#introduction)
- [Architecture Overview](#architecture-overview)
- [Key Features](#key-features)
- [Core Components](#core-components)
- [Tool Ecosystem](#tool-ecosystem)
- [LLM Provider Support](#llm-provider-support)
- [User Interface](#user-interface)
- [Configuration](#configuration)
- [Security & Safety](#security--safety)
- [Development & Testing](#development--testing)
- [Getting Started](#getting-started)

## Introduction

Andy CLI is a sophisticated **command-line AI assistant** built with .NET 8 that provides an interactive terminal user interface (TUI) for AI-powered code assistance and automation. It combines the power of modern Large Language Models (LLMs) with practical tool execution capabilities, wrapped in a professional terminal interface designed for developers and power users.

### What Makes Andy CLI Special

- **Multi-Provider Support**: Works seamlessly with 6 major LLM providers
- **Rich Tool Ecosystem**: 20+ built-in tools for common development tasks
- **Advanced Context Management**: Sophisticated conversation handling with safety mechanisms
- **Real-Time Monitoring**: Comprehensive instrumentation and debugging capabilities
- **Professional UI**: Modern terminal interface with excellent user experience
- **Extensible Architecture**: Easy to add new tools and providers

## Architecture Overview

Andy CLI follows a **modular, layered architecture** with clear separation of concerns:

### Framework Stack
- **Andy.Engine** - Core AI agent engine with tool execution
- **Andy.Llm** - LLM provider abstractions and implementations  
- **Andy.Tools** - Extensible tool framework for file operations, code search, etc.
- **Andy.Tui** - High-performance terminal UI framework
- **Andy.Model** - Shared models and abstractions

### Application Layers
- **Presentation Layer** - Terminal UI components and widgets
- **Application Layer** - Main orchestration and command handling
- **Service Layer** - Business logic and tool management
- **Framework Layer** - Core framework components
- **Tool Ecosystem** - Built-in and custom tools
- **LLM Provider Layer** - External AI service integrations

## Key Features

### 1. Multi-Provider LLM Support

Andy CLI supports 6 major LLM providers with automatic detection and switching:

- **OpenAI** (GPT-4, GPT-4o-mini) - Highest priority for reliability
- **Cerebras** - Fast Llama models with limited tool support
- **Anthropic** - Claude models with full tool support
- **Google Gemini** - Google's AI models
- **Ollama** - Local models via Ollama server
- **Azure OpenAI** - Enterprise OpenAI deployments

### 2. Smart Provider Detection

The system automatically detects and selects the best available LLM provider based on:
- Environment variables (API keys)
- Provider availability and connectivity
- Priority ranking system
- Ollama local server detection

### 3. Interactive Terminal UI (TUI)

Rich terminal interface featuring:
- Real-time streaming responses
- Syntax highlighting for code blocks
- Tool execution progress indicators
- Token usage counter
- Performance metrics overlay
- Intuitive keyboard navigation

### 4. Comprehensive Tool System

20+ built-in tools organized by category:
- **File System**: read_file, write_file, list_directory, copy_file, delete_file, move_file
- **Text Processing**: search_text, replace_text, format_text
- **System Tools**: bash_command, system_info, process_info
- **Development**: code_index, git_diff, create_directory
- **Web & JSON**: http_request, json_processor
- **Utilities**: datetime_tool, encoding_tool, todo_management

### 5. Advanced Tool Execution Architecture

Sophisticated tool execution system with:
- Multi-iteration conversation loop (up to 12 iterations)
- Safety mechanisms to prevent infinite loops
- Cumulative output tracking and limiting
- Tool call/response linking and context preservation
- Error handling and fallback mechanisms

### 6. Real-Time Instrumentation

Comprehensive monitoring and debugging:
- Real-time conversation flow visualization
- Tool execution tracking
- Performance metrics
- Error monitoring
- System prompt visibility
- Debug logging and tracing

## Core Components

### SimpleAssistantService
The main orchestration service that manages conversations with tool support. It handles:
- Message processing and context management
- Tool execution coordination
- LLM communication
- Response rendering and display

### ProviderDetectionService
Automatically detects available LLM providers based on environment variables and connectivity. Provides:
- Priority-based provider selection
- Diagnostic information
- Provider availability checking
- Ollama server detection

### ToolRegistry
Central registry for managing all available tools. Features:
- Tool registration and discovery
- Metadata management
- Configuration handling
- Search and filtering capabilities

### ContentPipeline
Handles response processing and rendering:
- Markdown processing
- Code block extraction and highlighting
- Content sanitization
- Feed rendering

### InstrumentationServer
Real-time monitoring and debugging server:
- Web-based dashboard (port 5555)
- Conversation flow visualization
- Performance metrics
- Error tracking

## Tool Ecosystem

### File System Tools
- **read_file**: Read file contents with encoding detection
- **write_file**: Write content to files with proper encoding
- **list_directory**: List directory contents with filtering
- **copy_file**: Copy files with metadata preservation
- **delete_file**: Safely delete files with confirmation
- **move_file**: Move/rename files and directories

### Text Processing Tools
- **search_text**: Search within files using regex patterns
- **replace_text**: Find and replace text with validation
- **format_text**: Format text content (JSON, XML, etc.)

### System Tools
- **bash_command**: Execute shell commands safely
- **system_info**: Get system information and status
- **process_info**: Process information and monitoring

### Development Tools
- **code_index**: Index and search code repositories
- **git_diff**: Show git differences and changes
- **create_directory**: Create directories with proper permissions

### Web & JSON Tools
- **http_request**: Make HTTP requests with various methods
- **json_processor**: Process and validate JSON data

### Utility Tools
- **datetime_tool**: Date/time operations and formatting
- **encoding_tool**: Text encoding detection and conversion
- **todo_management**: Task management and tracking

## LLM Provider Support

### OpenAI
- **Models**: GPT-4, GPT-4o, GPT-4o-mini
- **Features**: Full tool support, structured output
- **Priority**: Highest (most reliable)
- **Configuration**: OPENAI_API_KEY, OPENAI_API_BASE

### Cerebras
- **Models**: Llama-3.3-70b (function calling enabled)
- **Features**: Fast inference, limited to 4 essential tools
- **Priority**: High (fast performance)
- **Configuration**: CEREBRAS_API_KEY

### Anthropic
- **Models**: Claude-3-Sonnet, Claude-3-Haiku
- **Features**: Full tool support, excellent reasoning
- **Priority**: High
- **Configuration**: ANTHROPIC_API_KEY

### Google Gemini
- **Models**: Gemini-2.0-Flash, Gemini-Pro
- **Features**: Full tool support, multimodal capabilities
- **Priority**: Medium
- **Configuration**: GOOGLE_API_KEY

### Ollama
- **Models**: Local models (Llama2, CodeLlama, etc.)
- **Features**: Full tool support, no API key required
- **Priority**: Medium (local deployment)
- **Configuration**: OLLAMA_API_BASE

### Azure OpenAI
- **Models**: GPT-4, GPT-3.5-turbo
- **Features**: Enterprise features, custom endpoints
- **Priority**: Medium
- **Configuration**: AZURE_OPENAI_API_KEY, AZURE_OPENAI_ENDPOINT

## User Interface

### Terminal UI Features
- **Real-time Streaming**: Live response updates as they arrive
- **Syntax Highlighting**: Code blocks with language-specific highlighting
- **Progress Indicators**: Visual feedback for tool execution
- **Token Counter**: Real-time token usage tracking
- **Performance HUD**: Optional performance metrics overlay

### Keyboard Shortcuts
- `Ctrl+P` - Open command palette
- `F2` - Toggle performance HUD
- `ESC` - Exit application
- `Up/Down` - Scroll through chat history
- `Page Up/Down` - Fast scroll
- `Ctrl+]` - Toggle scroll mode (Feed ↔ Prompt History)

### Scroll Modes
- **Feed Mode** (default): Scrolls conversation history
- **Prompt History Mode**: Navigate through previous user messages

### Command Palette
Accessible via `Ctrl+P`, provides quick access to:
- Model switching and information
- Tool management and execution
- Chat management (clear, help)
- UI controls (HUD toggle)

## Configuration

### Environment Variables

#### Required for Providers
- `OPENAI_API_KEY` - OpenAI API key
- `CEREBRAS_API_KEY` - Cerebras API key
- `ANTHROPIC_API_KEY` - Anthropic API key
- `GOOGLE_API_KEY` - Google API key
- `AZURE_OPENAI_API_KEY` - Azure OpenAI API key
- `AZURE_OPENAI_ENDPOINT` - Azure OpenAI endpoint URL

#### Provider Control
- `ANDY_SKIP_OLLAMA=1` - Skip Ollama even if running locally
- `ANDY_OLLAMA_AUTO_DETECT=false` - Disable automatic Ollama detection

#### Optional Configuration
- `OPENAI_API_BASE` - Custom OpenAI API endpoint
- `OLLAMA_API_BASE` - Custom Ollama endpoint
- `ANDY_DEBUG=true` - Enable debug logging
- `ANDY_STRICT_ERRORS=1` - Enable strict error handling

### Configuration File (appsettings.json)
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

## Security & Safety

### Alpha Release Warnings
⚠️ **IMPORTANT**: This software is in ALPHA stage with critical warnings:

- **DESTRUCTIVE OPERATIONS**: Performs file and directory operations
- **NOT PRODUCTION READY**: Do not use in production environments
- **NO GUARANTEES**: No guarantees about functionality, stability, or safety
- **USE AT YOUR OWN RISK**: Authors assume no responsibility for data loss

### Security Features
- **Permission Profiles**: Tool execution permission management
- **Resource Monitoring**: CPU and memory usage tracking
- **Security Manager**: Tool execution security controls
- **Output Limiting**: Prevents context overflow attacks
- **Input Validation**: Parameter validation and sanitization

### Safety Mechanisms
- **Maximum Iterations**: Prevents infinite conversation loops (12 max)
- **Consecutive Tool Detection**: Breaks after 3 consecutive tool-only iterations
- **Force Text Response**: Forces text summary after multiple tool iterations
- **Fallback Mechanisms**: Graceful degradation on errors
- **Error Policies**: Strict vs lenient error handling

## Development & Testing

### Test Structure
- **Unit Tests**: Individual component testing
- **Integration Tests**: LLM provider integration
- **Tool Tests**: Tool execution and validation
- **UI Tests**: Terminal interface components
- **End-to-End Tests**: Complete conversation flows

### Testing Commands
```bash
# Run all tests (skip Ollama detection for CI)
ANDY_SKIP_OLLAMA=1 dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Generate coverage report
reportgenerator -reports:"./TestResults/*/coverage.cobertura.xml" -targetdir:"./TestResults/CoverageReport" -reporttypes:Html
```

### Debug Features
- **Raw Response Logging**: Log LLM responses to files
- **Conversation Tracing**: Track conversation flow
- **Message Debugging**: Debug message parts and structure
- **Tool Execution Traces**: Monitor tool execution
- **Context State Changes**: Track context modifications

## Getting Started

### Prerequisites
- .NET 8.0 SDK
- At least one LLM provider API key
- Terminal with ANSI color support

### Installation
```bash
# Clone the repository
git clone <repository-url>
cd andy-cli

# Build the solution
dotnet build

# Run the application
dotnet run --project src/Andy.Cli
```

### Quick Start
1. **Set API Key**: Export your preferred provider's API key
   ```bash
   export OPENAI_API_KEY="your-api-key"
   ```

2. **Run Application**: Start the interactive TUI
   ```bash
   dotnet run --project src/Andy.Cli
   ```

3. **First Interaction**: Try asking a question
   ```
   What files are in the current directory?
   ```

4. **Explore Commands**: Use slash commands
   ```
   /model list
   /tools list
   /help
   ```

### Example Usage
```bash
# Interactive mode
dotnet run --project src/Andy.Cli

# Command line mode
dotnet run --project src/Andy.Cli -- model list
dotnet run --project src/Andy.Cli -- tools info read_file

# With specific provider
ANDY_SKIP_OLLAMA=1 dotnet run --project src/Andy.Cli
```

---

*This documentation provides a comprehensive overview of the Andy CLI solution. For detailed technical specifications, see the Architecture Documentation and API Reference.*
