# Tool Execution Architecture in AiConversationService

## Overview

The `AiConversationService` orchestrates complex interactions between users, LLMs, and tools through a sophisticated conversation loop. This document explains how tools are integrated, executed, and managed within the conversation flow.

## Architecture Components

### Core Components

- **AiConversationService**: Main orchestrator for conversations with tool support
- **ContextManager**: Maintains conversation history (user messages, assistant responses, tool results)
- **ConversationContext** (andy-llm): Manages the actual message list sent to the LLM
- **ToolExecutionService**: Handles tool execution with security and resource management
- **ToolRegistry**: Provides available tools and their metadata
- **CumulativeOutputTracker**: Prevents context overflow from tool outputs

## Request Flow

### 1. Initialization & Context Setup

When a conversation begins:
1. System prompt is built with available tool definitions from ToolRegistry
2. ContextManager initializes with the system prompt
3. ContentPipeline is created for rendering responses

```
ToolRegistry → System Prompt → ContextManager → ConversationContext
```

### 2. Request Building Process

```
User Message → ContextManager → ConversationContext → LlmRequest
```

**Steps:**
1. User message is added to ContextManager
2. Tool definitions are included in the request (unless it's a simple greeting)
3. Provider-specific adjustments:
   - Cerebras: Limited to 4 essential tools (to avoid 400 errors)
   - Other providers: All enabled tools included
4. Empty message parts are fixed to ensure valid requests

### 3. LLM Communication

The service supports two communication modes:

#### Streaming (Preferred)
- Receives structured `FunctionCall` objects directly from the LLM
- Better for tool scenarios as it provides structured data
- Accumulates function calls in `_pendingFunctionCalls` list

#### Non-Streaming
- Gets complete response at once
- Falls back to text parsing for tool detection

## The Main Conversation Loop

The service runs up to **12 iterations**, allowing for complex multi-step operations:

```python
While (iteration < maxIterations):
    1. Build request with current context
    2. Send request to LLM
    3. Receive response (streaming or non-streaming)
    4. Parse response for tool calls
    5. If tool calls found:
        a. Execute each tool
        b. Add results to context
        c. Continue loop for next iteration
    6. If text only:
        a. Display content to user
        b. Break loop (conversation complete)
```

### Loop Safety Mechanisms

- **Maximum iterations**: 12 (prevents infinite loops)
- **Consecutive tool-only detection**: Breaks after 3 consecutive iterations with only tool calls
- **Force text response**: After multiple tool iterations, removes tools from request to force a text summary
- **Fallback mechanism**: If request fails with tools, retries without them

## Tool Call Detection & Execution

### Structured Path (Streaming)

When using streaming, tool calls arrive as structured data:

```csharp
// FunctionCall objects come directly from LLM
await foreach (var chunk in _llmClient.StreamCompleteAsync(request, cancellationToken))
{
    if (chunk.FunctionCall != null)
    {
        _pendingFunctionCalls.Add(chunk.FunctionCall);
    }
}
```

Each tool call receives a unique ID (e.g., "call_" + GUID) for tracking.

### Tool Execution Flow

```csharp
For each tool call:
    1. Validate parameters using ToolCallValidator
    2. Repair parameters if invalid (single attempt)
    3. Execute via ToolExecutionService
    4. Apply output limits (cumulative tracking)
    5. Add result to context as ToolResponsePart
```

### Validation & Repair

Before execution, tool parameters are validated:
- Check required parameters
- Verify parameter types
- Attempt auto-repair for common issues
- Log warnings for invalid parameters

## Context Management for Tool Calls

The context maintains proper message sequencing for tool interactions:

```yaml
User: "What files are in the src directory?"
Assistant (with tool_calls): "I'll list the directory for you"
Tool (tool_response): [directory contents]
Assistant: "Here are the files in src:..."
```

**Critical Requirements:**
- Assistant messages with tool calls MUST include the tool_calls in context
- Tool responses must be properly linked to their call IDs
- If there's no text with tool calls, use placeholder text like "(Executing tools...)"

### Message Structure

```csharp
// Assistant message with tool calls
_contextManager.AddAssistantMessage(
    text: "I'll check that for you",
    toolCalls: [
        { CallId: "call_abc123", ToolId: "list_directory", Parameters: {...} }
    ]
);

// Tool response
_contextManager.AddToolExecution(
    toolId: "list_directory",
    callId: "call_abc123",
    parameters: {...},
    output: "file1.txt\nfile2.txt..."
);
```

## Output Management

### CumulativeOutputTracker

Prevents tool output from overwhelming the context window:

- **Total limit**: 6000 characters across all tools in one turn
- **Per-tool limits**:
  - `list_directory`: 5000 chars
  - Other tools: 2000 chars default
- **Dynamic adjustment**: Reduces limits as cumulative usage approaches maximum

### Output Truncation Strategy

```csharp
var baseLimit = ToolOutputLimits.GetLimit(toolId);
var adjustedLimit = _outputTracker.GetAdjustedLimit(toolId, baseLimit);
var limitedOutput = ToolOutputLimits.LimitOutput(toolId, outputStr, adjustedLimit);
_outputTracker.RecordOutput(toolId, limitedOutput.Length);
```

## Response Compilation & Display

### ContentPipeline Processing

1. Text responses are rendered through ContentPipeline
2. Code blocks are extracted and displayed with syntax highlighting
3. Tool outputs are formatted and truncated if needed
4. Final response combines all text portions from iterations

### Display Flow

```
Raw Response → Parser → AST → Renderer → FeedView → User Display
```

## Error Handling

### Tool Execution Errors

- Captured and added as error responses to context
- Displayed to user with error message
- Conversation continues if possible

### Parameter Validation Errors

- Auto-repair attempted for malformed parameters
- Single repair attempt per tool call
- Logged warnings for tracking

### Network/Provider Errors

- Fallback to no-tools mode on provider errors
- Retry logic for transient failures
- Error policy (strict vs lenient) determines behavior

### Error Policy

```csharp
// Strict mode: Errors are rethrown
ErrorPolicy.RethrowIfStrict(ex);

// Lenient mode: Errors are logged and handled gracefully
_logger?.LogError(ex, "Error executing tool");
```

## Provider-Specific Handling

### Cerebras
- Limited to 4 essential tools
- Tools: `list_directory`, `read_file`, `bash_command`, `search_files`
- Prevents 400 Bad Request errors

### OpenAI/Anthropic
- Full tool set available
- Better structured output support
- Native function calling

### Qwen
- Custom parser for Qwen-specific format
- Special handling for thought blocks

## Key Design Decisions

### 1. Streaming Preferred
- Better for structured tool calls from modern LLMs
- Reduces parsing complexity
- More reliable tool detection

### 2. Context Preservation
- Every tool call and response is tracked
- Maintains conversation continuity
- Enables multi-step reasoning

### 3. Progressive Enhancement
- Simple greetings skip tools entirely
- Complex queries get full tool support
- Adaptive based on user intent

### 4. Defensive Programming
- Multiple fallbacks for provider limitations
- Graceful degradation on errors
- Comprehensive logging for debugging

### 5. Token Management
- Aggressive output limiting
- Cumulative tracking across tools
- Stays within context windows

## Configuration

### Environment Variables

- `ANDY_DEBUG_RAW`: Enable raw response logging
- `ANDY_TRACE`: Enable conversation tracing
- `ANDY_STRICT_ERRORS`: Enable strict error mode
- `ANDY_DEBUG_MESSAGE_PARTS`: Enable message debugging

### Limits Configuration

```csharp
// Maximum iterations for conversation loop
private const int MaxIterations = 12;

// Maximum consecutive tool-only iterations
private const int MaxConsecutiveToolIterations = 3;

// Cumulative output limits
private const int MaxCumulativeOutput = 6000;
private const int PerToolLimitMultiple = 800;
```

## Debugging

### Log Files

Responses are logged to:
```
~/.andy/logs/llm/YYYY-MM-DD/response_HH-mm-ss_fff.txt
```

### Debug Output

Enable debug mode to see:
- Request/response details
- Tool execution traces
- Context state changes
- Parameter validation results

## Best Practices

### For Tool Implementation

1. Keep tool outputs concise
2. Return structured data when possible
3. Handle errors gracefully
4. Validate inputs thoroughly

### For Conversation Flow

1. Let the LLM decide when to use tools
2. Don't force tool usage for simple queries
3. Allow multi-step planning
4. Provide clear tool descriptions

### For Context Management

1. Monitor token usage
2. Truncate large outputs appropriately
3. Preserve essential information
4. Clean up after tool execution

## Troubleshooting

### Common Issues

**Issue**: Tools not being called
- Check if request includes tool definitions
- Verify provider supports function calling
- Ensure tools are enabled in registry

**Issue**: Context overflow
- Check CumulativeOutputTracker limits
- Verify output truncation is working
- Consider reducing tool output verbosity

**Issue**: Infinite tool loops
- Check MaxIterations setting
- Verify consecutive tool detection
- Ensure proper response handling

**Issue**: Provider errors with tools
- Check provider-specific limits
- Verify tool parameter formats
- Consider fallback strategies

## Future Improvements

- [ ] Parallel tool execution support
- [ ] Tool dependency resolution
- [ ] Smarter output summarization
- [ ] Context compression strategies
- [ ] Tool result caching
- [ ] Advanced retry strategies

## Related Documentation

- [Tool Development Guide](./tool-development.md)
- [Provider Configuration](./providers.md)
- [System Prompts](./system-prompts.md)
- [Error Handling](./error-handling.md)