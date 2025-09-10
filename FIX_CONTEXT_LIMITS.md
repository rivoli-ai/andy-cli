# Fix for Tool Output Truncation Issue (Updated)

## Problem
When asking "what's in the current repo", the LLM would read a file (like Program.cs) and the tool result would be heavily truncated at 2000 characters, causing:
1. Incomplete information for the LLM to work with
2. Malformed truncation messages that could confuse JSON parsing
3. HTTP 400 errors when the truncated content led to invalid follow-up requests

## Solution Implemented (Version 2 - Per-Tool Limits)

### 1. Increased Tool Result Limit
**File**: `src/Andy.Cli/Services/ContextManager.cs`
- Changed `maxToolResultChars` from 2000 to 15000 characters
- This allows reading reasonable-sized code files without truncation
- Most source files under 15KB will now be read in full

### 2. Improved Truncation Format
**File**: `src/Andy.Cli/Services/ContextManager.cs`
- Changed truncation message format from:
  ```
  [Output truncated: showing X of Y characters. Z characters omitted.]
  ```
- To cleaner format:
  ```
  ... (output truncated - showing first 15000 of Y total characters)
  ```
- This avoids bracket notation that might confuse JSON parsing

### 3. Increased Context Token Limits
**File**: `src/Andy.Cli/Services/ContextManager.cs`
- Changed `maxTokens` default from 6000 to 30000
- Changed `compressionThreshold` from 4500 to 25000
- This allows for longer conversations before compression is needed
- Modern LLMs can handle much larger contexts than the old limits

## Impact
- File reading operations now return more complete content
- Reduced likelihood of truncation-related parsing errors
- Better context retention for longer conversations
- Cleaner truncation messages when limits are exceeded

## Testing
To verify the fix works:
1. Ask Andy CLI to read a large file (e.g., "what's in Program.cs")
2. The response should include much more of the file content
3. No HTTP 400 errors should occur from truncated tool results

## Additional Improvements Made
- Created comprehensive conversation tracing system (ConversationTracer)
- Added environment variables for debugging:
  - `ANDY_TRACE=1` - Enable trace logging to file
  - `ANDY_TRACE_CONSOLE=1` - Also output trace to console
- Helper scripts for tracing:
  - `run-with-trace.sh` - Start Andy with tracing
  - `view-trace.sh` - View trace files with formatting