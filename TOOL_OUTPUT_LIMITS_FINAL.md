# Tool Output Limiting Solution - Final Implementation

## Problem
Queries like "what's in src" were causing HTTP 400 errors because tool outputs (especially from `list_directory` with recursive listing) were overwhelming the LLM context.

## Solution: Aggressive Per-Tool Output Limiting

### Key Components

#### 1. ToolOutputLimits Service (`src/Andy.Cli/Services/ToolOutputLimits.cs`)
Implements tool-specific output limits with conservative values:

| Tool | Limit | Reason |
|------|-------|--------|
| `read_file` | 3000 chars | File content preview |
| `list_directory` | 1500 chars | Directories can be huge |
| `code_index` | 2500 chars | Code search results |
| `search_files` | 2000 chars | Search results |
| `search_text` | 2000 chars | Text search results |
| `bash`/`bash_command` | 2000 chars | Command output |
| `http_request` | 3000 chars | Web responses |
| `web_search` | 2000 chars | Search results |
| Default | 2000 chars | Conservative fallback |

#### 2. Application Points (`src/Andy.Cli/Services/AiConversationService.cs`)
Tool outputs are limited BEFORE being added to context:
```csharp
var limitedOutput = ToolOutputLimits.LimitOutput(callToExecute.ToolId, outputStr);
_contextManager.AddToolExecution(callToExecute.ToolId, thisCallId, callToExecute.Parameters, limitedOutput);
```

#### 3. Secondary Safety Net (`src/Andy.Cli/Services/ContextManager.cs`)
- Secondary cap at 3000 chars (down from 15000)
- Context limits: 12000 max tokens, 10000 compression threshold
- Ensures no tool result exceeds safe limits

### Features
- **Smart truncation**: Attempts to truncate at natural boundaries (newlines/spaces)
- **Informative messages**: Includes tool name and character count in truncation message
- **Debug logging**: Logs when truncation occurs for debugging
- **Null safety**: Handles null/empty outputs gracefully

### Testing
Comprehensive test suite verifies:
- Correct limits per tool type
- Proper truncation behavior
- Natural boundary detection
- Different limits for different tools

## Results
With these aggressive limits in place:
- Tool outputs are kept manageable (1500-3000 chars max)
- LLM context doesn't get overwhelmed
- HTTP 400 errors should be eliminated
- LLM still gets enough information to be helpful

## How It Works
1. User asks "what's in src"
2. LLM calls `list_directory` tool
3. Tool might produce 50KB of output
4. `ToolOutputLimits.LimitOutput()` caps it to 1500 chars
5. Limited output is added to context
6. LLM receives manageable content and can respond without errors

## Key Insight
The solution is to limit tool outputs BEFORE they enter the context, not after. This prevents the context from ever becoming too large, avoiding provider limits and parsing issues.