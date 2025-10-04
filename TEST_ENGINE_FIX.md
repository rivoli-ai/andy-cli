# Testing Andy-Engine Chat Fix

## Changes Made

Modified `/Users/sami/devel/rivoli-ai/andy-engine/src/Andy.Engine/Planner/LlmPlanner.cs`:

Added fallback handling for when LLM returns `{"ask_user": {"question": "...", "missing_fields": []}}` instead of `{"action": "ask_user", ...}`.

**Key Logic:**
- When `missing_fields` is empty and `question` is present, treat it as a conversational response
- Convert to `StopDecision(question)` so the response content flows to the user

## Expected Behavior

**Before fix:**
```
Error: Missing 'action' field in response: {"ask_user":{"question":"Hello! How can I assist you today?","missing_fields":[]}}
```

**After fix:**
```
Hello! How can I assist you today?
```

## Test Commands

```bash
# Build andy-engine
cd /Users/sami/devel/rivoli-ai/andy-engine
dotnet build src/Andy.Engine/Andy.Engine.csproj

# Build andy-cli
cd /Users/sami/devel/rivoli-ai/andy-cli
dotnet build src/Andy.Cli/Andy.Cli.csproj

# Run andy-cli
export OPENAI_API_KEY=your_key
dotnet run --project src/Andy.Cli/Andy.Cli.csproj

# Test simple greeting
> hello
```

## Notes

The fix handles conversational responses by detecting when the LLM returns an `ask_user` format with no missing fields - this indicates a chat response rather than a request for information.

If this works, we should also update the Planner prompt to be more explicit about the expected format, or accept that LLMs will sometimes use alternative formats and handle them gracefully (which is what we're doing now).
