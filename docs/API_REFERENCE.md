# Andy CLI API Reference

## Table of Contents
- [Core Services](#core-services)
- [Tool Framework](#tool-framework)
- [LLM Provider Interface](#llm-provider-interface)
- [UI Components](#ui-components)
- [Configuration](#configuration)
- [Events and Callbacks](#events-and-callbacks)
- [Error Handling](#error-handling)

## Core Services

### SimpleAssistantService

Main orchestration service for AI conversations with tool support.

**Location**: `src/Andy.Cli/Services/SimpleAssistantService.cs`

#### Constructor
```csharp
public SimpleAssistantService(
    ILlmProvider llmProvider,
    IToolRegistry toolRegistry,
    IToolExecutor toolExecutor,
    FeedView feed,
    string modelName,
    string providerName,
    TokenCounter? tokenCounter = null,
    ILogger<SimpleAssistantService>? logger = null)
```

#### Methods

##### ProcessMessageAsync
```csharp
public async Task<string> ProcessMessageAsync(
    string userMessage,
    bool enableStreaming = false,
    CancellationToken cancellationToken = default)
```
**Description**: Process a user message through the AI assistant with tool support.

**Parameters**:
- `userMessage` (string): The user's input message
- `enableStreaming` (bool): Whether to enable streaming responses (currently ignored)
- `cancellationToken` (CancellationToken): Cancellation token

**Returns**: `Task<string>` - The assistant's response

**Example**:
```csharp
var response = await assistantService.ProcessMessageAsync("What files are in the current directory?");
```

##### ClearContext
```csharp
public void ClearContext()
```
**Description**: Clear the conversation context and reset token counters.

##### GetContextStats
```csharp
public ContextStats GetContextStats()
```
**Description**: Get statistics about the current conversation context.

**Returns**: `ContextStats` object with:
- `TurnCount` (int): Number of conversation turns
- `EstimatedTokens` (int): Estimated token usage
- `TotalDuration` (TimeSpan): Total conversation duration
- `LastInputTokens` (int): Last input token count
- `LastOutputTokens` (int): Last output token count

### ProviderDetectionService

Service for detecting available LLM providers based on environment variables.

**Location**: `src/Andy.Cli/Services/ProviderDetectionService.cs`

#### Methods

##### DetectDefaultProvider
```csharp
public string? DetectDefaultProvider()
```
**Description**: Detects the default provider based on available environment variables.

**Returns**: `string?` - Name of the detected provider, or null if none found

**Provider Priority**:
1. OpenAI (priority 1)
2. Anthropic (priority 2)
3. Cerebras (priority 3)
4. Gemini (priority 4)
5. Ollama (priority 5)
6. Azure (priority 6)

##### GetAvailableProviders
```csharp
public List<string> GetAvailableProviders()
```
**Description**: Gets all available providers ordered by priority.

**Returns**: `List<string>` - List of available provider names

##### IsProviderAvailable
```csharp
public bool IsProviderAvailable(string providerName)
```
**Description**: Checks if a specific provider is available.

**Parameters**:
- `providerName` (string): Name of the provider to check

**Returns**: `bool` - True if provider is available

##### GetDiagnosticInfo
```csharp
public string GetDiagnosticInfo()
```
**Description**: Gets diagnostic information about provider detection.

**Returns**: `string` - Formatted diagnostic information

## Tool Framework

### IToolRegistry

Interface for managing tool registrations and discovery.

**Location**: `src/Andy.Cli/Services/ToolRegistry.cs`

#### Methods

##### RegisterTool
```csharp
public ToolRegistration RegisterTool<T>(Dictionary<string, object?>? configuration = null) where T : class, ITool
public ToolRegistration RegisterTool(Type toolType, Dictionary<string, object?>? configuration = null)
public ToolRegistration RegisterTool(ToolMetadata metadata, Func<IServiceProvider, ITool> factory, Dictionary<string, object?>? configuration = null)
```
**Description**: Register a tool with the registry.

**Parameters**:
- `T` or `toolType`: Tool type implementing ITool
- `metadata`: Tool metadata
- `factory`: Factory function for creating tool instances
- `configuration`: Optional configuration dictionary

**Returns**: `ToolRegistration` - Registration information

##### GetTools
```csharp
public IReadOnlyList<ToolRegistration> GetTools(
    ToolCategory? category = null,
    ToolCapability? capabilities = null,
    IEnumerable<string>? tags = null,
    bool enabledOnly = true)
```
**Description**: Get tools matching the specified criteria.

**Parameters**:
- `category`: Filter by tool category
- `capabilities`: Filter by required capabilities
- `tags`: Filter by tags
- `enabledOnly`: Only return enabled tools

**Returns**: `IReadOnlyList<ToolRegistration>` - Matching tools

##### CreateTool
```csharp
public ITool? CreateTool(string toolId, IServiceProvider serviceProvider)
```
**Description**: Create a tool instance by ID.

**Parameters**:
- `toolId`: Tool identifier
- `serviceProvider`: Service provider for dependency injection

**Returns**: `ITool?` - Tool instance or null if not found

### ITool

Interface that all tools must implement.

#### Properties

##### Metadata
```csharp
public ToolMetadata Metadata { get; }
```
**Description**: Tool metadata including ID, name, description, and category.

#### Methods

##### ExecuteAsync
```csharp
public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> parameters)
```
**Description**: Execute the tool with the provided parameters.

**Parameters**:
- `parameters`: Dictionary of parameter name-value pairs

**Returns**: `Task<ToolResult>` - Tool execution result

### ToolMetadata

Metadata structure for tools.

```csharp
public class ToolMetadata
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public ToolCategory Category { get; set; }
    public ToolCapability RequiredCapabilities { get; set; }
    public string[] Tags { get; set; }
    public Dictionary<string, ToolParameterInfo> Parameters { get; set; }
}
```

### ToolResult

Result structure for tool execution.

```csharp
public class ToolResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public object? Data { get; set; }
    public Dictionary<string, object?>? Metadata { get; set; }
}
```

## LLM Provider Interface

### ILlmProvider

Interface for LLM provider implementations.

#### Methods

##### CompleteAsync
```csharp
public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
```
**Description**: Complete a conversation with the LLM.

**Parameters**:
- `request`: LLM request with messages and configuration
- `cancellationToken`: Cancellation token

**Returns**: `Task<LlmResponse>` - LLM response

##### StreamCompleteAsync
```csharp
public IAsyncEnumerable<LlmStreamChunk> StreamCompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
```
**Description**: Stream a conversation with the LLM.

**Parameters**:
- `request`: LLM request with messages and configuration
- `cancellationToken`: Cancellation token

**Returns**: `IAsyncEnumerable<LlmStreamChunk>` - Stream of response chunks

##### ListModelsAsync
```csharp
public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
```
**Description**: List available models for this provider.

**Parameters**:
- `cancellationToken`: Cancellation token

**Returns**: `Task<IEnumerable<ModelInfo>>` - Available models

### LlmRequest

Request structure for LLM communication.

```csharp
public class LlmRequest
{
    public Message[] Messages { get; set; }
    public LlmClientConfig Config { get; set; }
    public ToolDefinition[]? Tools { get; set; }
}
```

### LlmResponse

Response structure from LLM communication.

```csharp
public class LlmResponse
{
    public Message? AssistantMessage { get; set; }
    public FunctionCall[]? FunctionCalls { get; set; }
    public LlmUsage? Usage { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
```

## UI Components

### FeedView

Main conversation display component.

#### Methods

##### AddUserMessage
```csharp
public void AddUserMessage(string message, int? messageNumber = null)
```
**Description**: Add a user message to the feed.

##### AddMarkdownRich
```csharp
public void AddMarkdownRich(string content)
```
**Description**: Add markdown content to the feed with rich formatting.

##### AddCode
```csharp
public void AddCode(string code, string? language = null)
```
**Description**: Add code block to the feed with syntax highlighting.

##### AddToolExecutionStart
```csharp
public void AddToolExecutionStart(string toolId, string toolName, Dictionary<string, object?> parameters)
```
**Description**: Add tool execution start indicator.

##### AddToolExecutionComplete
```csharp
public void AddToolExecutionComplete(string toolId, bool success, string duration, string? result = null)
```
**Description**: Add tool execution completion indicator.

### PromptLine

Input component for user text entry.

#### Methods

##### OnKey
```csharp
public string? OnKey(ConsoleKeyInfo keyInfo)
```
**Description**: Handle keyboard input.

**Returns**: `string?` - Submitted text or null if not submitted

##### SetText
```csharp
public void SetText(string text)
```
**Description**: Set the current text content.

##### SetShowCaret
```csharp
public void SetShowCaret(bool show)
```
**Description**: Show or hide the text cursor.

### CommandPalette

Command selection and execution interface.

#### Methods

##### Open
```csharp
public void Open()
```
**Description**: Open the command palette.

##### Close
```csharp
public void Close()
```
**Description**: Close the command palette.

##### SetCommands
```csharp
public void SetCommands(CommandItem[] commands)
```
**Description**: Set available commands.

##### ExecuteSelectedAsync
```csharp
public Task ExecuteSelectedAsync()
```
**Description**: Execute the currently selected command.

## Configuration

### LlmOptions

Configuration options for LLM providers.

```csharp
public class LlmOptions
{
    public string DefaultProvider { get; set; } = "openai";
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
}
```

### ProviderConfig

Configuration for individual providers.

```csharp
public class ProviderConfig
{
    public string? ApiKey { get; set; }
    public string? ApiBase { get; set; }
    public string? Model { get; set; }
    public bool Enabled { get; set; } = true;
}
```

### ToolFrameworkOptions

Configuration options for the tool framework.

```csharp
public class ToolFrameworkOptions
{
    public bool RegisterBuiltInTools { get; set; } = true;
    public bool EnableObservability { get; set; } = true;
    public bool AutoDiscoverTools { get; set; } = true;
}
```

## Events and Callbacks

### Tool Execution Events

#### ToolCalled Event
```csharp
public event EventHandler<ToolCalledEventArgs>? ToolCalled;
```

**EventArgs**:
```csharp
public class ToolCalledEventArgs : EventArgs
{
    public string ToolName { get; set; }
    public Dictionary<string, object?> Parameters { get; set; }
    public string CallId { get; set; }
}
```

#### ToolCompleted Event
```csharp
public event EventHandler<ToolCompletedEventArgs>? ToolCompleted;
```

**EventArgs**:
```csharp
public class ToolCompletedEventArgs : EventArgs
{
    public string ToolName { get; set; }
    public bool Success { get; set; }
    public object? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan Duration { get; set; }
}
```

### Registry Events

#### ToolRegistered Event
```csharp
public event EventHandler<ToolRegisteredEventArgs>? ToolRegistered;
```

#### ToolUnregistered Event
```csharp
public event EventHandler<ToolUnregisteredEventArgs>? ToolUnregistered;
```

## Error Handling

### ErrorPolicy

Utility class for error handling policies.

#### RethrowIfStrict
```csharp
public static void RethrowIfStrict(Exception exception)
```
**Description**: Rethrow exception if strict error mode is enabled.

### Common Exceptions

#### ToolExecutionException
```csharp
public class ToolExecutionException : Exception
{
    public string ToolId { get; }
    public Dictionary<string, object?>? Parameters { get; }
    
    public ToolExecutionException(string toolId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ToolId = toolId;
    }
}
```

#### ProviderException
```csharp
public class ProviderException : Exception
{
    public string ProviderName { get; }
    
    public ProviderException(string providerName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderName = providerName;
    }
}
```

### Error Handling Patterns

#### Graceful Degradation
```csharp
try
{
    var result = await tool.ExecuteAsync(parameters);
    return result;
}
catch (Exception ex)
{
    _logger?.LogError(ex, "Tool execution failed");
    return ToolResult.Failure($"Tool execution failed: {ex.Message}");
}
```

#### Retry Logic
```csharp
for (int attempt = 0; attempt < maxRetries; attempt++)
{
    try
    {
        return await provider.CompleteAsync(request);
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
    {
        if (attempt < maxRetries - 1)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            continue;
        }
        throw;
    }
}
```

---

*This API reference provides detailed technical specifications for developers working with or extending the Andy CLI solution.*
