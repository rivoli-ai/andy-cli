# Fix for Tool Output Truncation Issue - Version 2

## Problem
When asking "what's in the current repo", the LLM would read files and receive massive outputs that could overwhelm the context window, causing HTTP 400 errors.

## Solution: Per-Tool Output Limiting

### 1. Created ToolOutputLimits Service
**File**: `src/Andy.Cli/Services/ToolOutputLimits.cs`
- Defines specific output limits for each tool type:
  - `read_file`: 5000 characters
  - `search_files`: 3000 characters  
  - `search_text`: 3000 characters
  - `list_directory`: 2000 characters
  - `code_index`: 4000 characters
  - `bash_command`: 3000 characters
  - `http_request`: 5000 characters
  - `web_search`: 3000 characters
  - Default: 4000 characters

### 2. Applied Limits Before Context Addition
**File**: `src/Andy.Cli/Services/AiConversationService.cs`
- Tool outputs are now limited BEFORE being added to context
- Applied at three key points where `AddToolExecution` is called
- Uses `ToolOutputLimits.LimitOutput()` to cap output size

### 3. Adjusted Context Manager Limits
**File**: `src/Andy.Cli/Services/ContextManager.cs`
- Reduced `maxTokens` to 12000 (from 30000)
- Reduced `compressionThreshold` to 10000 (from 25000)
- Reduced secondary `maxToolResultChars` to 6000 (safety net)
- These are now reasonable since tool outputs are pre-limited

### 4. Smart Truncation
The `LimitOutput` method:
- Truncates at natural boundaries (newlines/spaces) when possible
- Adds informative truncation message with tool name
- Preserves null/empty outputs unchanged

## Benefits
1. **Predictable context usage** - Each tool has appropriate limits
2. **Better LLM performance** - Avoids overwhelming with huge outputs
3. **Tool-specific optimization** - File reads get more space than simple commands
4. **Clean truncation** - Natural boundaries and clear messages
5. **Prevents HTTP 400 errors** - Context stays within provider limits

## Testing
All tests pass for the new ToolOutputLimits service:
- Correct limits per tool type
- Proper truncation of large outputs
- Natural boundary truncation
- Different limits for different tools

## Usage
When Andy CLI reads a file or executes a command:
1. Tool executes and produces output
2. `ToolOutputLimits.LimitOutput()` caps the output size
3. Limited output is added to context
4. LLM receives manageable, focused content

This approach ensures the LLM gets enough information to be helpful while preventing context overflow issues.