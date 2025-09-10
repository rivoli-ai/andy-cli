# Testing the Conversation Tracing System

## Setup

The conversation tracing system has been successfully integrated into Andy CLI with the following features:

### 1. ConversationTracer Class
- Located in: `src/Andy.Cli/Services/ConversationTracer.cs`
- Logs all interactions between app, tools, and LLM
- Outputs to JSON file in `/tmp/andy-trace-*.json` format
- Optional console output with `ANDY_TRACE_CONSOLE=1`

### 2. Integration Points
- `AiConversationService` class has tracer integrated at all key points:
  - User message input
  - LLM request preparation
  - LLM response handling
  - Tool call extraction
  - Tool execution
  - Context statistics
  - Error handling

### 3. Environment Variables
- `ANDY_TRACE=1` - Enable tracing to file
- `ANDY_TRACE_CONSOLE=1` - Also output to console
- `ANDY_TRACE_PATH=/path/to/trace.json` - Custom trace file path

## Manual Testing Instructions

To test the tracing system:

1. **Start Andy with tracing enabled:**
   ```bash
   cd /Users/sami/devel/rivoli-ai/andy-cli
   ANDY_TRACE=1 dotnet run --project src/Andy.Cli
   ```

2. **In the interactive TUI:**
   - Type a simple query like "What time is it?"
   - Press Enter to send
   - Watch for the trace message in console (shows trace file location)
   - Try a few more queries that use tools

3. **Exit Andy:**
   - Press ESC to exit

4. **View the trace file:**
   ```bash
   ./view-trace.sh
   ```
   Or manually:
   ```bash
   ls -la /tmp/andy-trace-*.json
   cat /tmp/andy-trace-*.json | jq .
   ```

## Expected Trace Output

The trace file should contain entries like:
- `trace_start` - Session initialization
- `user_message` - User queries
- `llm_request` - Requests sent to LLM
- `llm_response` - LLM responses
- `tool_calls_extracted` - Tool calls parsed from response
- `tool_execution` - Tool execution details
- `context_stats` - Token usage statistics
- `iteration` - Processing loop iterations
- `trace_end` - Session termination

## Helper Scripts

1. **run-with-trace.sh** - Starts Andy with tracing enabled
2. **view-trace.sh** - Pretty-prints trace files with color coding

## Verification

The tracing system is working if:
1. Trace files are created in `/tmp/andy-trace-*.json`
2. Console shows `[TRACE] Logging to: /tmp/andy-trace-*.json` at startup
3. Each user interaction generates multiple trace entries
4. Tool executions are logged with parameters and results
5. Errors and context statistics are captured

## Current Status

✅ Tracing system implemented and integrated
✅ Successfully builds without errors  
✅ Trace file creation verified (startup event logged)
⏳ Full interactive testing needed to verify all trace points

The system is ready for manual testing in interactive mode.