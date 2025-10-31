# Andy CLI Architecture Documentation

## Table of Contents
- [Architecture Overview](#architecture-overview)
- [Component Architecture](#component-architecture)
- [Data Flow](#data-flow)
- [Tool Execution Architecture](#tool-execution-architecture)
- [Context Management](#context-management)
- [Provider Integration](#provider-integration)
- [Security Architecture](#security-architecture)
- [Performance Considerations](#performance-considerations)
- [Extension Points](#extension-points)

## Architecture Overview

Andy CLI follows a **layered, modular architecture** designed for maintainability, extensibility, and performance. The architecture separates concerns across multiple layers while maintaining clear interfaces between components.

### Architectural Principles

1. **Separation of Concerns**: Each layer has distinct responsibilities
2. **Dependency Injection**: Loose coupling through DI container
3. **Interface Segregation**: Small, focused interfaces
4. **Open/Closed Principle**: Open for extension, closed for modification
5. **Single Responsibility**: Each component has one clear purpose

### Layer Structure

```
┌─────────────────────────────────────────────────────────────┐
│                    PRESENTATION LAYER                       │
│  Terminal UI │ Command Palette │ Feed View │ Widgets       │
└─────────────────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────────────────┐
│                    APPLICATION LAYER                        │
│  Program.cs │ Commands │ Services │ Orchestration          │
└─────────────────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────────────────┐
│                     SERVICE LAYER                           │
│  Tool Registry │ Executor │ Context Manager │ Pipeline     │
└─────────────────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────────────────┐
│                    FRAMEWORK LAYER                          │
│  Andy.Engine │ Andy.Llm │ Andy.Tools │ Andy.Tui │ Andy.Model│
└─────────────────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────────────────┐
│                   EXTERNAL LAYER                           │
│  LLM Providers │ File System │ Network │ System APIs       │
└─────────────────────────────────────────────────────────────┘
```

## Component Architecture

### Core Components

#### 1. Program.cs (Main Entry Point)
**Location**: `src/Andy.Cli/Program.cs`
**Responsibilities**:
- Application initialization and configuration
- Service container setup
- Terminal UI initialization
- Main event loop management
- Command-line argument handling

**Key Dependencies**:
- ServiceCollection for DI
- Andy.Tui for terminal interface
- ProviderDetectionService for LLM setup

#### 2. SimpleAssistantService
**Location**: `src/Andy.Cli/Services/SimpleAssistantService.cs`
**Responsibilities**:
- Main conversation orchestration
- Tool execution coordination
- LLM communication management
- Response processing and rendering
- Context state management

**Key Methods**:
```csharp
public async Task<string> ProcessMessageAsync(string userMessage, bool enableStreaming = false)
public void ClearContext()
public ContextStats GetContextStats()
```

#### 3. ProviderDetectionService
**Location**: `src/Andy.Cli/Services/ProviderDetectionService.cs`
**Responsibilities**:
- Automatic LLM provider detection
- Environment variable validation
- Provider priority management
- Diagnostic information generation

**Key Methods**:
```csharp
public string? DetectDefaultProvider()
public List<string> GetAvailableProviders()
public bool IsProviderAvailable(string providerName)
public string GetDiagnosticInfo()
```

#### 4. ToolRegistry
**Location**: `src/Andy.Cli/Services/ToolRegistry.cs`
**Responsibilities**:
- Tool registration and management
- Tool discovery and metadata
- Configuration management
- Search and filtering capabilities

**Key Methods**:
```csharp
public ToolRegistration RegisterTool<T>(Dictionary<string, object?>? configuration = null)
public IReadOnlyList<ToolRegistration> GetTools(ToolCategory? category = null, bool enabledOnly = true)
public ITool? CreateTool(string toolId, IServiceProvider serviceProvider)
```

#### 5. ToolCatalog
**Location**: `src/Andy.Cli/Services/ToolCatalog.cs`
**Responsibilities**:
- Centralized tool registration
- Trim-safe tool discovery
- Built-in tool management
- Service collection configuration

**Key Methods**:
```csharp
public static void RegisterAllTools(IServiceCollection services)
private static void RegisterTool<TTool>(IServiceCollection services)
```

### Framework Components

#### Andy.Engine
**Purpose**: Core AI agent engine with tool execution capabilities
**Key Features**:
- SimpleAgent for conversation management
- Tool execution coordination
- Context management
- Multi-turn conversation support

#### Andy.Llm
**Purpose**: LLM provider abstractions and implementations
**Key Features**:
- Provider factory pattern
- Unified API across providers
- Configuration management
- Streaming support

#### Andy.Tools
**Purpose**: Extensible tool framework
**Key Features**:
- Tool interface definitions
- Built-in tool implementations
- Security and permission management
- Resource monitoring

#### Andy.Tui
**Purpose**: High-performance terminal UI framework
**Key Features**:
- Terminal capability detection
- Efficient rendering pipeline
- Input handling
- Layout management

## Data Flow

### Main Conversation Flow

```
User Input
    ↓
Terminal UI (PromptLine)
    ↓
SimpleAssistantService.ProcessMessageAsync()
    ↓
ContentPipeline Creation
    ↓
SimpleAgent.ProcessMessageAsync()
    ↓
LLM Provider Communication
    ↓
Response Processing
    ↓
Tool Call Detection
    ↓
Tool Execution (if needed)
    ↓
Context Update
    ↓
Content Pipeline Processing
    ↓
Feed View Rendering
    ↓
User Display
```

### Tool Execution Flow

```
Tool Call Detection
    ↓
Parameter Validation
    ↓
Tool Registry Lookup
    ↓
Tool Instance Creation
    ↓
Security Manager Check
    ↓
Resource Monitor Check
    ↓
Tool Execution
    ↓
Output Limiting
    ↓
Result Processing
    ↓
Context Update
    ↓
UI Update
```

### Context Management Flow

```
User Message
    ↓
Context Manager
    ↓
Conversation Context
    ↓
System Prompt + Tool Definitions
    ↓
LLM Request Building
    ↓
Provider-Specific Adjustments
    ↓
Request Validation
    ↓
LLM Communication
    ↓
Response Processing
    ↓
Tool Results Integration
    ↓
Context Update
```

## Tool Execution Architecture

### Multi-Iteration Conversation Loop

The system supports complex multi-step operations through a sophisticated conversation loop:

```csharp
// Simplified loop structure
for (int iteration = 0; iteration < MaxIterations; iteration++)
{
    // 1. Build request with current context
    var request = BuildRequest(context);
    
    // 2. Send to LLM
    var response = await llmProvider.CompleteAsync(request);
    
    // 3. Process response
    if (response.HasToolCalls)
    {
        // Execute tools and continue loop
        await ExecuteTools(response.ToolCalls);
        continue;
    }
    else
    {
        // Display final response and exit
        DisplayResponse(response);
        break;
    }
}
```

### Safety Mechanisms

1. **Maximum Iterations**: Prevents infinite loops (12 iterations max)
2. **Consecutive Tool Detection**: Breaks after 3 consecutive tool-only iterations
3. **Force Text Response**: Removes tools from request after multiple iterations
4. **Fallback Mechanism**: Retries without tools if request fails
5. **Output Limiting**: Prevents context overflow

### Tool Call Processing

```csharp
// Tool call processing pipeline
foreach (var toolCall in response.ToolCalls)
{
    // 1. Validate parameters
    var validationResult = ValidateParameters(toolCall);
    
    // 2. Repair if needed (single attempt)
    if (!validationResult.IsValid)
    {
        toolCall = RepairParameters(toolCall);
    }
    
    // 3. Execute tool
    var result = await toolExecutor.ExecuteAsync(toolCall);
    
    // 4. Apply output limits
    var limitedResult = ApplyOutputLimits(result);
    
    // 5. Add to context
    context.AddToolResult(toolCall.Id, limitedResult);
}
```

## Context Management

### Message Structure

The context maintains proper message sequencing for tool interactions:

```csharp
// Message sequence example
User: "What files are in the src directory?"
Assistant (with tool_calls): "I'll list the directory for you"
Tool (tool_response): [directory contents]
Assistant: "Here are the files in src:..."
```

### Context Components

#### 1. ConversationContext (Andy.Llm)
- Manages the actual message list sent to LLM
- Handles message formatting and validation
- Maintains conversation history

#### 2. ContextManager (Andy.Cli)
- High-level context management
- Tool result integration
- System prompt management
- Context statistics

#### 3. CumulativeOutputTracker
- Prevents tool output from overwhelming context
- Tracks cumulative output across tools
- Applies dynamic limits based on usage

### Context Preservation

```csharp
// Critical requirements for context management
- Assistant messages with tool calls MUST include the tool_calls in context
- Tool responses must be properly linked to their call IDs
- If there's no text with tool calls, use placeholder text
- Maintain proper message ordering
- Preserve conversation continuity across iterations
```

## Provider Integration

### Provider Factory Pattern

```csharp
public interface ILlmProviderFactory
{
    ILlmProvider CreateProvider(string providerName);
    IEnumerable<string> GetAvailableProviders();
}
```

### Provider Detection Logic

```csharp
// Provider detection priority
1. OpenAI (priority 1) - Most reliable
2. Anthropic (priority 2) - Excellent reasoning
3. Cerebras (priority 3) - Fast inference
4. Gemini (priority 4) - Google's offering
5. Ollama (priority 5) - Local deployment
6. Azure (priority 6) - Enterprise option
```

### Provider-Specific Handling

#### Cerebras Limitations
- Limited to 4 essential tools to prevent 400 errors
- Tools: list_directory, read_file, bash_command, search_files
- Fast inference but restricted functionality

#### OpenAI/Anthropic
- Full tool set available
- Better structured output support
- Native function calling

#### Ollama
- Local deployment, no API key required
- Full tool support
- Requires local Ollama server

## Security Architecture

### Security Layers

1. **Permission Profiles**: Tool execution permissions
2. **Resource Monitoring**: CPU and memory limits
3. **Security Manager**: Tool execution controls
4. **Input Validation**: Parameter validation and sanitization
5. **Output Limiting**: Prevents context overflow attacks

### Security Components

#### SecurityManager
```csharp
public interface ISecurityManager
{
    Task<bool> CanExecuteToolAsync(string toolId, Dictionary<string, object?> parameters);
    Task<SecurityResult> ValidateToolExecutionAsync(ToolExecutionRequest request);
}
```

#### ResourceMonitor
```csharp
public interface IResourceMonitor
{
    Task<bool> CheckResourceLimitsAsync();
    Task RecordResourceUsageAsync(string toolId, ResourceUsage usage);
}
```

### Security Policies

- **Strict Mode**: Errors are rethrown, strict validation
- **Lenient Mode**: Errors are logged, graceful degradation
- **Tool Permissions**: Per-tool execution permissions
- **Resource Limits**: CPU, memory, and execution time limits

## Performance Considerations

### Optimization Strategies

1. **Streaming Responses**: Real-time updates without waiting for complete response
2. **Tool Caching**: Cache tool instances and configurations
3. **Context Compression**: Smart context management to stay within limits
4. **Lazy Loading**: Load providers and tools on demand
5. **Output Limiting**: Prevent context overflow with smart truncation

### Performance Metrics

- **Response Time**: LLM response latency
- **Tool Execution Time**: Individual tool performance
- **Memory Usage**: Context and tool memory consumption
- **Token Usage**: Input/output token tracking
- **Iteration Count**: Conversation loop efficiency

### Monitoring

```csharp
// Performance monitoring via InstrumentationServer
- Real-time conversation flow
- Tool execution tracking
- Performance metrics collection
- Error rate monitoring
- Resource usage tracking
```

## Extension Points

### Adding New Tools

1. **Implement ITool Interface**:
```csharp
public class MyCustomTool : ITool
{
    public ToolMetadata Metadata => new()
    {
        Id = "my_custom_tool",
        Name = "My Custom Tool",
        Description = "Description of what this tool does",
        Category = ToolCategory.Utilities
    };
    
    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        // Tool implementation
    }
}
```

2. **Register in ToolCatalog**:
```csharp
RegisterTool<MyCustomTool>(services);
```

### Adding New Providers

1. **Implement ILlmProvider Interface**
2. **Add Provider Factory Support**
3. **Update ProviderDetectionService**
4. **Add Configuration Support**

### Custom UI Components

1. **Implement Widget Interface**
2. **Add to Service Collection**
3. **Integrate with Main UI**

---

*This architecture documentation provides detailed technical specifications for understanding and extending the Andy CLI solution.*
