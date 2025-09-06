# Qwen Tool Call Fix Documentation

## Problem
When using the qwen-3-coder-480b model with Cerebras provider, tool calls were not being executed properly. The model was outputting tool calls in a generic JSON format `{"tool":"...", "parameters":{...}}` that wasn't being recognized and extracted correctly.

## Symptoms
1. User would see raw JSON output instead of tool execution
2. The JSON would appear as `{"tool":"system_info","parameters":{}}` in the console
3. Tool execution counter showed "0 tool calls" even though JSON was visible
4. User would see "SIMULATED BY LLM" messages

## Root Causes

### 1. Pattern Matching Issue
The regex pattern for extracting generic format tool calls was too restrictive:
- Old pattern: `\{[^{}]*"tool"\s*:\s*"([^"]+)"[^{}]*"parameters"\s*:\s*(\{[^{}]*\})[^{}]*\}`
- This pattern couldn't handle empty parameters `{}` correctly due to `[^{}]*` within the parameters capture group

### 2. Display Logic Issue
The AiConversationService was displaying raw JSON when tool calls were found, instead of cleaning the response and showing only the descriptive text.

## Solution

### 1. Fixed Pattern Matching
Updated the regex pattern in ModelResponseInterpreter.cs to properly handle empty parameters:
```csharp
var genericPattern = @"\{\s*""tool""\s*:\s*""([^""]+)""\s*,\s*""parameters""\s*:\s*(\{[^}]*\})\s*\}";
```
This pattern:
- Uses `[^}]*` instead of `[^{}]*` within parameters to allow empty objects
- Properly captures both empty `{}` and populated parameters

### 2. Improved Display Logic
Changed AiConversationService to:
- Use the ModelResponseInterpreter's `CleanResponseForDisplay` method when tool calls are found
- Remove the logic that was displaying raw JSON to users
- Only show the cleaned descriptive text from the LLM

### 3. Enhanced Cleaning
Updated the cleaning patterns to remove the generic format tool calls from display:
```csharp
response = Regex.Replace(response, @"\{\s*""tool""\s*:\s*""[^""]+""\s*,\s*""parameters""\s*:\s*\{[^}]*\}\s*\}", "", 
    RegexOptions.Singleline);
```

## Qwen Tool Call Formats Supported

Based on documentation and testing, the interpreter now supports:

### 1. Preferred Format (JSON-in-XML)
```xml
<tool_call>
{"name":"function_name","arguments":{"param":"value"}}
</tool_call>
```

### 2. Generic Format (Fallback)
```json
{"tool":"function_name","parameters":{"param":"value"}}
```

### 3. Empty Parameters
Both formats work with empty parameters:
- `{"name":"system_info","arguments":{}}`
- `{"tool":"system_info","parameters":{}}`

## Testing
Created comprehensive tests in:
- `QwenSimpleResponseTest.cs` - Basic extraction tests
- `QwenFullFlowTest.cs` - End-to-end flow tests

All tests verify:
1. Tool calls are correctly extracted
2. Response text is properly cleaned
3. Multiple tool calls in one response work
4. Both formats are supported

## Debug Mode
Added environment variable support for debugging:
- Set `ANDY_DEBUG_RAW=true` to see raw LLM output
- Raw output appears in code blocks marked as `raw-llm-output`

## Additional Fixes

### Streaming Display Issue
When using streaming mode, raw JSON was being displayed as it arrived:
- Updated `GetLlmStreamingResponseAsync` to detect tool calls after streaming completes
- If tool calls are detected, hide the raw streamed content and show cleaned text
- This prevents users from seeing raw JSON during streaming

### System Prompt Updates for Qwen
Updated the system prompt specifically for Qwen models to:
- Explicitly instruct to NOT use tools for simple greetings
- Support both `<tool_call>` and generic `{"tool":"..."}` formats
- Emphasize using one tool at a time
- Prevent duplicated/corrupted output

### Empty Response Handling
When Qwen outputs ONLY a tool call with no accompanying text:
- The cleaned response returns empty string
- This lets the tool execution output speak for itself
- Prevents showing placeholder text when only tools are being used

## Verification
To verify the fix works:
1. Switch to qwen-3-coder-480b model
2. Say "hello" or ask "what's in the current directory?"
3. Tool calls should execute properly without showing JSON
4. No "SIMULATED BY LLM" messages should appear
5. No duplicated or corrupted text should appear
6. Simple greetings should not trigger tool usage