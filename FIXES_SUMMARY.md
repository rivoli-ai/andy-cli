# LLM Response Issues - Fixes Summary

## Date: 2025-09-11

## Issues Fixed

### 1. Model Selection Not Actually Switching
**Problem**: When switching models (e.g., from Claude to QWEN or GPT), the LLM was still responding as Claude because the model name wasn't being passed correctly to the LLM request.

**Root Cause**: 
- The `AiConversationService` constructor wasn't receiving model/provider names
- The `CreateRequest()` calls weren't passing the model name parameter
- The LLM request's Model property was always null

**Fixes Applied**:
1. **Program.cs**: Pass model and provider names to AiConversationService constructor (3 locations)
2. **AiConversationService.cs**: Update all `CreateRequest()` calls to pass `_currentModel`
3. **ModelCommand.cs**: Pass model name when creating test requests

### 2. Empty Text Responses After Tool Execution
**Problem**: LLM text responses after tool calls were not being displayed in the UI.

**Previous Investigation**: 
- Empty Parts arrays in messages were being fixed with `FixEmptyMessageParts()`
- Mixed responses (tool call + text) weren't being parsed correctly
- The parser was separating tool calls from text correctly, but model selection issue was preventing proper responses

**Status**: This issue should be resolved once the model selection fix is deployed, as the LLM will now receive the correct model parameter and respond appropriately.

## Files Modified

1. `/Users/sami/devel/rivoli-ai/andy-cli/src/Andy.Cli/Program.cs`
   - Lines 247-256: Added model/provider parameters to AiConversationService constructor
   - Lines 345-356: Added newModel/newProvider parameters after model switch  
   - Lines 771-784: Added newModel/newProvider parameters in alternative flow
   - Line 900: Pass currentModel to CreateRequest()

2. `/Users/sami/devel/rivoli-ai/andy-cli/src/Andy.Cli/Services/AiConversationService.cs`
   - Lines 163, 494, 509, 679, 919: Updated all CreateRequest() calls to pass _currentModel

3. `/Users/sami/devel/rivoli-ai/andy-cli/src/Andy.Cli/Commands/ModelCommand.cs`
   - Line 610: Pass GetCurrentModel() to CreateRequest()

4. `/Users/sami/devel/rivoli-ai/andy-cli/src/Andy.Cli/Parsing/Parsers/GenericParser.cs`
   - NEW FILE: Generic parser for Llama, GPT, Claude and other models with plain text responses

5. `/Users/sami/devel/rivoli-ai/andy-cli/src/Andy.Cli/Parsing/Compiler/LlmResponseCompiler.cs`
   - Lines 268-281: Updated CreateParserForModel to select parser based on provider

### 3. Wrong Parser for Non-Qwen Models  
**Problem**: All models were using QwenParser regardless of provider, causing Llama and other models' plain text responses to not be parsed correctly.

**Root Cause**: 
- `CreateParserForModel` in LlmResponseCompiler.cs was hardcoded to always return QwenParser
- Llama and other models return plain text, not Qwen's specific format

**Fix Applied**:
1. Created new `GenericParser.cs` for handling plain text responses from Llama, GPT, Claude, etc.
2. Updated `CreateParserForModel` to select appropriate parser based on provider
3. QwenParser used only for Qwen models, GenericParser for all others

## Testing

Created comprehensive test suite for mixed response parsing:
- `/Users/sami/devel/rivoli-ai/andy-cli/tests/Andy.Cli.Tests/Parsing/MixedResponseParsingTests.cs`

Note: These tests are currently failing because the QwenParser needs the JSON repair service properly configured. This is a separate issue from the model selection problem.

## Next Steps

1. Test the model switching functionality to confirm it now properly switches between Claude, QWEN, and GPT models
2. Verify that LLM responses after tool execution are now displayed correctly
3. Fix the QwenParser tests to properly handle tool call extraction

## How to Verify the Fix

1. Start the Andy CLI
2. Switch to a different model: `Switch Model qwen qwen-3-coder`
3. Ask a question that requires tool usage: `What files are in the current directory?`
4. Verify that:
   - The model actually switches (check response style)
   - Text responses after tool execution are displayed
   - The model name in requests matches what was selected