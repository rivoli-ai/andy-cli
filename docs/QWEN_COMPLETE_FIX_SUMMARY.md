# Complete Qwen Model Integration Fixes

## Issues Resolved

### 1. Tool Calls Not Executing
**Problem**: Qwen outputs tool calls as `{"tool":"...", "parameters":{...}}` but they weren't being extracted
**Solution**: Fixed regex pattern to handle both empty `{}` and nested parameters correctly

### 2. Raw JSON Displayed During Streaming
**Problem**: Users saw raw JSON tool calls being streamed to display
**Solution**: Hide streaming content and show cleaned text when tool calls are detected

### 3. Escaped JSON in Tool Results
**Problem**: Qwen was outputting tool results as escaped JSON strings (e.g., `\u0022`, `\\n`)
**Solution**: 
- Simplified tool result formatting for Qwen to prevent re-encoding
- Added detection and removal of escaped JSON sequences
- Clear responses that are entirely escaped JSON

### 4. Excessive Tool Usage
**Problem**: Qwen would use multiple tools for simple greetings
**Solution**: Updated system prompt to explicitly discourage tool usage for greetings

### 5. Duplicated/Corrupted Text
**Problem**: Text was appearing multiple times and corrupted during streaming
**Solution**: 
- Implemented paragraph-level deduplication
- Added line-level duplicate detection
- Normalized text comparison to catch partial duplicates

## Technical Changes

### ModelResponseInterpreter.cs - QwenInterpreter

1. **Tool Call Extraction**:
```csharp
// Now handles both formats properly
var genericPattern = @"\{\s*""tool""\s*:\s*""([^""]+)""\s*,\s*""parameters""\s*:\s*(\{[^}]*\})\s*\}";
```

2. **Tool Result Formatting**:
- Simplifies JSON results to prevent Qwen from re-outputting them
- Special handling for list_directory to show items clearly
- Returns empty string when response is only a tool call

### AiConversationService.cs

1. **Streaming Improvements**:
- Detects tool calls after streaming completes
- Hides raw JSON from display
- Shows only cleaned content

### SystemPromptService.cs

1. **Qwen-Specific Instructions**:
- Don't use tools for greetings
- Support both tool call formats
- Use one tool at a time
- Be concise and avoid repetition

## Supported Qwen Tool Call Formats

### Format A (Preferred XML):
```xml
<tool_call>
{"name":"tool_name","arguments":{"param":"value"}}
</tool_call>
```

### Format B (Generic JSON):
```json
{"tool":"tool_name","parameters":{"param":"value"}}
```

Both formats work with:
- Empty parameters: `{}`
- Nested parameters: `{"operation":"get_current_time"}`
- Multiple parameters: `{"path":".", "recursive":true}`

## Testing

Created comprehensive test suite:
- `QwenSimpleResponseTest.cs` - Basic tool extraction
- `QwenFullFlowTest.cs` - End-to-end scenarios  
- `QwenDateTimeTest.cs` - Nested parameter handling

All 110 tests passing.

## Debug Mode

Set `ANDY_DEBUG_RAW=true` environment variable to see:
- Raw LLM output before processing
- Tool call extraction details
- Response cleaning steps

## Known Qwen Behaviors

1. **Tool-Happy**: Qwen tends to use tools even when not necessary
2. **Format Preference**: Uses generic JSON format more than XML tags
3. **Result Echo**: May try to echo tool results if they're too complex
4. **Streaming Quirks**: Outputs complete JSON in chunks during streaming

## Verification Steps

1. Start CLI with Qwen model:
   ```bash
   dotnet run -- --provider cerebras --model qwen-3-coder-480b
   ```

2. Test simple greeting:
   - Type: `hello`
   - Expected: Greeting response without tool usage
   - No JSON should be visible

3. Test tool usage:
   - Type: `list the current directory`
   - Expected: Tool executes and shows results
   - No raw JSON visible

4. Test multiple tools:
   - Type: `show system info and list files`
   - Expected: Tools execute sequentially
   - Clean output without duplication

## Future Improvements

1. Consider caching model-specific interpreters
2. Add telemetry to track tool call patterns
3. Implement rate limiting for excessive tool usage
4. Add model-specific configuration files