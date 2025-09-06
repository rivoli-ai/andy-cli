# Qwen-Code Implementation Comparison

## Overview
Analysis of how qwen-code (Google's implementation) handles Qwen model integration compared to our andy-cli implementation.

## Key Findings

### 1. Architecture Approach

**qwen-code:**
- Uses OpenAI API format via DashScope (Alibaba's API gateway)
- Extends `OpenAIContentGenerator` class
- Treats Qwen as OpenAI-compatible through DashScope endpoint
- Base URL: `https://dashscope.aliyuncs.com/compatible-mode/v1`

**andy-cli:**
- Direct model-specific interpreters (`QwenInterpreter`)
- Handles both XML (`<tool_call>`) and JSON formats
- Custom response cleaning and deduplication

### 2. Tool Call Handling

**qwen-code:**
```typescript
// Standard OpenAI format
tool_calls: OpenAIToolCall[]
// Accumulated during streaming
streamingToolCalls: Map<number, AccumulatedToolCall>
```
- Uses standard OpenAI `tool_calls` format
- Accumulates tool calls during streaming with index tracking
- Validates tool call completeness with JSON parsing
- Clears accumulated calls after processing

**andy-cli:**
```csharp
// Supports two formats
// Format A: <tool_call>{...}</tool_call>
// Format B: {"tool":"...","parameters":{...}}
```
- Dual format support for flexibility
- Regex-based extraction
- Handles empty parameters explicitly

### 3. Response Cleaning

**qwen-code:**
- No specific Qwen response cleaning
- Relies on OpenAI API format standardization
- DashScope presumably handles model-specific quirks

**andy-cli:**
- Extensive response cleaning:
  - Removes duplicate paragraphs
  - Handles escaped JSON (`\u0022`, `\\n`)
  - Removes instructional text
  - Deduplicates at line and paragraph level

### 4. Streaming Handling

**qwen-code:**
```typescript
// Accumulates tool arguments incrementally
if (toolCall.function?.arguments) {
  accumulatedCall.arguments += toolCall.function.arguments;
}
```
- Incremental accumulation of tool arguments
- Validates JSON completeness before emitting
- Resets on new tool calls with same name

**andy-cli:**
```csharp
// Hides raw JSON during streaming
if (toolCalls.Any()) {
  streamingMessage.Hide();
  _feed.AddMarkdownRich(cleanedContent);
}
```
- Actively hides raw JSON from display
- Post-processes after streaming completes
- Replaces with cleaned content

### 5. Authentication & Configuration

**qwen-code:**
- OAuth2 authentication with token refresh
- Shared token manager for consistency
- Metadata injection for session tracking

**andy-cli:**
- API key based authentication
- Model-specific system prompts
- Direct API calls without gateway

## Key Differences

### 1. Abstraction Level
- **qwen-code**: High abstraction through OpenAI compatibility layer
- **andy-cli**: Low-level, model-specific handling

### 2. Response Processing
- **qwen-code**: Minimal processing, relies on API standardization
- **andy-cli**: Extensive cleaning and deduplication

### 3. Tool Call Formats
- **qwen-code**: Single format (OpenAI standard)
- **andy-cli**: Multiple format support for flexibility

### 4. Error Handling
- **qwen-code**: OAuth token refresh, auth error suppression
- **andy-cli**: Pattern-based detection and correction

## Recommendations

### What We're Doing Right
1. ✅ Comprehensive response cleaning
2. ✅ Multiple format support
3. ✅ Deduplication handling
4. ✅ Streaming display fixes

### What We Could Learn
1. **Tool Call Accumulation**: qwen-code's indexed accumulation during streaming is cleaner
2. **JSON Validation**: Checking for complete JSON before processing
3. **Session Metadata**: Including session/prompt IDs for tracking

### Missing Patterns
1. **Cache Control**: qwen-code adds cache control headers for DashScope
2. **Token Management**: Automatic refresh mechanism
3. **Metadata Tracking**: Session and prompt ID injection

## Conclusion

Our implementation is more defensive and handles edge cases that qwen-code delegates to the DashScope API gateway. This makes sense given:

1. **Direct API Access**: We're calling Qwen directly, not through DashScope
2. **Model Quirks**: We handle raw model output, not pre-processed responses
3. **User Experience**: We prioritize clean display over raw accuracy

The main advantage of qwen-code's approach is simplicity through standardization. Our advantage is robustness through explicit handling of model-specific behaviors.

## Action Items

1. ✅ Keep our defensive response cleaning
2. ✅ Maintain multiple format support
3. Consider adding:
   - JSON completeness validation
   - Indexed tool call accumulation
   - Session metadata tracking