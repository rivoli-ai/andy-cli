# Tool Execution Architecture in SimpleAssistantService

> Refreshed to match the current SimpleAssistantService-based architecture.
> The previous revision described an `AiConversationService` / `ToolExecutionService`
> / `ContextManager` design that has been replaced. The agent loop now lives in the
> Andy.Engine package (`SimpleAgent`); the CLI wraps it and adapts Andy.Tools tools
> into the engine's tool interface. Class names, tool names, and control flow below
> reflect the code as it currently exists.

## Overview

`SimpleAssistantService` is the interactive assistant service. It does NOT run the
agent loop itself: it constructs and wraps an `Andy.Engine.SimpleAgent`, which owns
the non-streaming LLM round-trip loop (request the model, run any tool calls it asks
for, feed the results back, repeat until the model returns a final answer or the
turn budget is exhausted). The CLI's job is to:

- build the system prompt and the tool catalog,
- adapt Andy.Tools tools into the engine's tool interface,
- gate every tool call through the Andy.Permissions engine,
- normalize/repair model-supplied parameters before dispatch,
- guard against repeated identical tool calls, and
- stream tool-execution state and the model's narration into the terminal feed.

## Architecture Components

### Core Components

- **SimpleAssistantService** (`src/Andy.Cli/Services/SimpleAssistantService.cs`):
  Interactive entry point. Creates the `SimpleAgent`, subscribes to its `ToolCalled`
  event, renders responses through the content pipeline, and reconciles tool UI state
  at the end of each turn.
- **SimpleAgent** (Andy.Engine NuGet package, not in this repo): Owns the agent loop.
  Non-streaming: `ProcessMessageAsync` returns a `SimpleAgentResult` (Success,
  Response, StopReason, TurnCount) and raises a `ToolCalled` event per tool call.
  Enforces a max-turns limit; on exhaustion it stops with StopReason
  `max_turns_exceeded`.
- **UiUpdatingToolExecutor** (`src/Andy.Cli/Services/UiUpdatingToolExecutor.cs`):
  Wraps the Andy.Tools `IToolExecutor`. Grants gated capability flags, maps/normalizes
  parameters, applies the repeated-identical-call loop guard, times execution, updates
  the feed, and forwards to the inner executor.
- **ToolAdapter / ToolRegistryAdapter** (`src/Andy.Cli/Services/Adapters/ToolAdapter.cs`):
  Adapts each Andy.Tools tool to the engine's `Andy.Model.Tooling.ITool` interface.
  Deserializes the model's JSON arguments, executes via the (UI-wrapping) executor,
  and serializes the result back as an `Andy.Model.Model.ToolResult`.
- **ParameterMapper** (`src/Andy.Cli/Services/ParameterMapper.cs`): Renames
  model-supplied parameter names to the tool's real names using a curated per-tool
  alias table and coerces value types. The live-dispatch path (`MapAndNormalize`) uses
  exact + hand-vetted aliases only (no fuzzy guessing).
- **ToolExecutionTracker** (`src/Andy.Cli/Services/ToolExecutionTracker.cs`):
  Singleton that shares tool-execution state between the adapter, the executor, and the
  feed. Maps tool names/correlation IDs to UI tool IDs and formats result summaries.
- **ToolCatalog** (`src/Andy.Cli/Services/ToolCatalog.cs`): Registers the tool catalog
  into DI as `ToolRegistrationInfo` entries, which drain into the `IToolRegistry`.
- **Andy.Permissions engine**: Gates each tool call (allow / ask / deny) according to
  layered rules.

## The Tool Catalog

`ToolCatalog.RegisterAllTools` registers the built-in surface. The shell tool is
`execute_command` (Andy.Tools' `ExecuteCommandTool`), permission-gated. The old
display-only `bash_command` tool has been retired. Other registered tools include:

- File system: `read_file`, `write_file`, `list_directory`, `copy_file`, `move_file`,
  `delete_file`, `create_directory`
- Text: `search_text`, `replace_text`, `format_text`
- Code/Git: `code_index`, `git_diff`
- System: `execute_command`, `process_info`, `system_info`
- Web/Data: `http_request`, `json_processor`, `dataframe_*` (DuckDB-backed)
- Utility: `datetime`, `encoding`, `todo_management`

The `ToolRegistryAdapter` wraps each enabled tool in a `ToolAdapter` and registers it
into an `Andy.Model.Tooling.ToolRegistry` that the engine consumes. Tool JSON schemas
are derived from each tool's `ToolMetadata` (parameter names, types, required flags,
enums, defaults).

## Initialization

When `SimpleAssistantService` is constructed:

1. The system prompt is built via `SystemPrompts.GetDefaultCliPrompt()` and stored in
   the instrumentation hub for the dashboard.
2. The injected `IToolExecutor` is wrapped in a `UiUpdatingToolExecutor`.
3. The injected `ILlmProvider` is wrapped in a `UsageTrackingLlmProvider`, which
   surfaces real per-round-trip token usage (`OnLlmUsage`) and the model's intermediate
   narration text (`OnIntermediateAssistantText`) while the turn runs.
4. A `SimpleAgent` is created with the usage-tracking provider, the tool registry, the
   UI-wrapping executor, the system prompt, `maxTurns: MaxAgentTurns` (50), and any
   provider-specific `extraBody`.
5. The service subscribes to `agent.ToolCalled` to create the UI entry for each call.

```
ILlmProvider --> UsageTrackingLlmProvider --\
IToolRegistry ----------------------------- SimpleAgent
IToolExecutor --> UiUpdatingToolExecutor ---/   ^
                                                |
SystemPrompts.GetDefaultCliPrompt() -----------/
```

## Request / Turn Flow

`ProcessMessageAsync(userMessage)` drives a single user turn:

1. A fresh content pipeline (markdown processor, sanitizer, feed renderer) is created.
2. Local shortcut: simple "what is the current directory" questions are answered
   without calling the LLM at all.
3. Live turn metrics begin and a processing indicator is shown in the feed.
4. An input-token estimate is seeded from history + the user message (replaced by real
   usage once the provider reports it).
5. `await _agent.ProcessMessageAsync(userMessage, ct)` runs the entire agent loop and
   returns a `SimpleAgentResult`. (The CLI does NOT loop here; the engine does.)
6. Any tools still marked running are reconciled and completed in the feed.
7. The response is selected (`SelectResponseContent`) and rendered through the pipeline.
8. The processing indicator is cleared and the response text is returned.

### Agent loop (inside SimpleAgent)

```
SimpleAgent.ProcessMessageAsync(userMessage):
    turn = 0
    loop:
        turn += 1
        if turn > maxTurns:
            stop with StopReason = "max_turns_exceeded"
        response = LLM.complete(history + tools)
        if response has tool calls:
            raise ToolCalled(toolName) for each call
            for each call:
                result = tool.ExecuteAsync(call)   # -> ToolAdapter
                append tool result to history
            continue          # next round-trip
        else:
            return final answer    # Success = true
```

Each round-trip that also issues tool calls carries the model's intermediate narration
("I'll first read the file...") which `UsageTrackingLlmProvider` forwards to the feed
before the tool executions, so the user sees reasoning live. The final answer (a
round-trip with no tool calls) is delivered only as the return value, so it is not
double-rendered.

## End-to-end Tool Call Sequence

This is the path for a single tool call within a turn:

```
SimpleAgent                 SimpleAssistantService        UiUpdatingToolExecutor /
(engine loop)               (ToolCalled handler)          ToolAdapter / Andy.Tools

  | model emits tool call
  |---- ToolCalled(name) -->|
  |                         | create unique UI tool id
  |                         | EnqueuePendingTool(name,id)
  |                         | register name/correlation mappings
  |                         | feed.AddToolExecutionStart(...)
  |
  | invoke ITool.ExecuteAsync(call)
  |------------------------------------------> ToolAdapter.ExecuteAsync
  |                                            | parse JSON args -> params
  |                                            | update UI with real params
  |                                            | track start (ToolExecutionTracker)
  |                                            | _toolExecutor.ExecuteAsync(toolId,...)
  |                                            |     -> UiUpdatingToolExecutor
  |                                            |        | grant gated capabilities
  |                                            |        | dequeue UI tool id
  |                                            |        | loop guard (identical call?)
  |                                            |        | ParameterMapper.MapAndNormalize
  |                                            |        | inner IToolExecutor.ExecuteAsync
  |                                            |        |   -> Andy.Permissions gate
  |                                            |        |   -> actual tool runs
  |                                            |        | track completion + stop spinner
  |                                            | ToolResult.FromObject(...)  (serialize)
  |<------------------------------------------ result back to model
  |
  | append ToolResult to history, continue loop
```

The inner `IToolExecutor.ExecuteAsync` is where the Andy.Permissions gate runs and the
real tool executes. `UiUpdatingToolExecutor.GrantGatedCapabilities` sets the capability
flags (FileSystem, Network, Process, Environment) on the execution context so the
lower-level capability checks do not pre-empt the permission gate; the gate remains the
actual consent authority (allow / ask / deny per call).

## Parameter Mapping and Normalization

Models frequently call a tool with parameter names borrowed from a different tool
family, or pass an array-typed parameter as a bare scalar. Before dispatch,
`UiUpdatingToolExecutor` calls `ParameterMapper.MapAndNormalize(toolId, params, metadata)`:

- Exact name matches pass through unchanged.
- Curated per-tool aliases rename known variants to the tool's real parameter names
  (for example, for `replace_text`: `old_string`/`new_string` -> `search_pattern`/
  `replacement_text`, and `file_path`/`path` -> `target_path`).
- Values are coerced to the declared type (e.g. a scalar string becomes a single-element
  array for array-typed parameters; "true"/"yes"/"1" become booleans).
- No fuzzy/Levenshtein matching is used on the dispatch path, so a call can never be
  mis-routed to the wrong parameter. Unrecognized names pass through unchanged.

(`ParameterMapper` also exposes `MapParameters`, which adds fuzzy matching, and
`NormalizeParameterTypes`, a value-only variant; the dispatch path uses the safe
`MapAndNormalize`.)

## Loop Guard

`UiUpdatingToolExecutor` holds a `ToolCallLoopDetector`. Before running a tool it
computes a signature from `toolId` + parameters. If the same signature has already been
seen repeatedly (the model is stuck re-issuing an identical call), the executor
short-circuits and returns a non-successful result with guidance text instead of
re-running the tool, telling the model to use the results it already has or change
approach. This caps wasted round-trips and token spend.

## Permissions

Tool calls are gated by the Andy.Permissions engine. Rules are resolved across layers,
lowest to highest precedence:

```
Builtin < User < Project < Local < Injected < Session < Managed
```

In the interactive CLI, a tool that resolves to "ask" triggers a permission prompt the
user must approve before the tool runs. Read-only tools generally stay auto-allowed;
mutating tools (write_file, delete_file, move_file, copy_file, replace_text,
create_directory) and `execute_command` typically prompt unless a higher-precedence
rule allows them.

## Result Handling and UI

- `ToolAdapter` converts the Andy.Tools `ToolExecutionResult` into an
  `Andy.Model.Model.ToolResult` (`ToolResult.FromObject(callId, name, data, isError)`),
  which is what flows back to the model.
- `UiUpdatingToolExecutor` extracts a human-readable summary from `result.Data`
  (tool-specific: line counts for `read_file`, match counts for `search_text`,
  structure/symbol/reference counts for `code_index`, item counts for `list_directory`,
  etc.) and stops the spinner the instant the tool returns.
- `ToolExecutionTracker.FormatResultSummary` produces feed summaries, including
  cleaning the `git_diff` tool's decorated output (stripping emoji/markdown, promoting
  the change-stat line, de-duplicating diff blocks) and surfacing real stdout/stderr +
  exit code for `execute_command`.
- `SimpleAssistantService` performs a backstop pass at end of turn to complete any tool
  still marked running, reading final state from `ToolExecutionTracker`.
  `AddToolExecutionComplete` is idempotent, so the earlier in-executor completion and
  this backstop do not conflict.

## Turn Limit Handling

`MaxAgentTurns` is 50. When the engine hits it, the result has StopReason
`max_turns_exceeded` and the engine packs the full conversation history into
`result.Response`. `SimpleAssistantService.SelectResponseContent` detects this
(`IsMaxTurnsExceeded`) and replaces the raw history dump with a concise notice so the
feed is not flooded with raw tool JSON; the returned string is likewise a short notice
rather than the dump.

## Token and Usage Tracking

- `UsageTrackingLlmProvider` fires `OnLlmUsage` with the provider's real prompt/
  completion token counts per round-trip; these feed the live turn stats and the
  session token counter.
- When a provider omits usage, the service falls back to a char/4 estimate at end of
  turn so the counters still move.
- `GetContextStats` reports turn count, an estimated token total from history, the last
  input/output token counts, and the model's context window
  (`ContextWindow.GetMaxTokens`).

## Headless Mode

`andy-cli run --headless --config <path>` runs the SAME engine via
`src/Andy.Cli/Headless/HeadlessAgentRunner.cs`. It:

1. Stands up a per-run `IServiceProvider` with Andy.Tools + Andy.Llm DI and registers
   the same built-in tool catalog (`ToolCatalog.RegisterAllTools`), plus any config
   cli/mcp tools via `HeadlessToolHost`.
2. Resolves the LLM provider from `config.model`.
3. Constructs `SimpleAgent` with `config.agent.instructions` as the system prompt and
   `config.limits.max_iterations` as `maxTurns` (default 10).
4. Runs with a wall-clock timeout; either limit firing maps to
   `HeadlessExitCode.Timeout` (4). Other outcomes map to structured `HeadlessExitCode`
   values; a max-turns stop (StopReason `max_turns`) is also treated as Timeout.
5. On success, atomically writes the final response to `config.output.file` and emits
   an NDJSON event envelope (`started` ... `finished`).

Headless wires permissions fail-closed (no interactive broker): anything that would
prompt is denied unless an injected per-run allow-list (`config.permissions.allowed_tools`,
installed at the Injected layer) or `ANDY_PERMISSION_MODE=bypass` relaxes it. See
`docs/headless-runtime.md` for the full headless contract.

### Headless tool wiring: MCP routing and CLI transport

`HeadlessToolHost.BuildAsync` turns each entry in `config.tools[]` into an `ITool`
adapter and registers it into the tool registry. No built-in tools are registered by
default -- the agent's tool surface is exactly what the config lists.

**MCP routing.** The top-level `mcp_gateway` config field (resolved from
`$ANDY_MCP_URL` via `ResolveMcpGateway`) provides a base URL for all MCP tools.
When an MCP tool binding omits `endpoint`, the runtime resolves it as
`{mcp_gateway}/{tool-name}`. A per-tool `endpoint` overrides the gateway when
both are present. One `McpClient` is created per distinct endpoint URL so adapters
sharing an endpoint reuse a single connection. Each MCP endpoint must advertise
the configured tool name or the run fails fast with `InvalidOperationException`.
When the `ANDY_TOKEN` env var is set, `HeadlessToolHost` attaches it as
`Authorization: Bearer <token>` on every MCP request.

**CLI transport.** Each CLI binding becomes a `CliSubprocessTool`. Two input modes
are supported (selected by `input_mode` in the tool config):

- **argv** (default): The LLM supplies an `args` string array. The runtime prepends
  `config.command` and passes the full argv via `ProcessStartInfo.ArgumentList` -- no
  shell, no string concatenation.
- **json**: The LLM supplies an `arguments` object (same parameter name as MCP tools).
  The runtime serializes it as JSON (UTF-8, no BOM, property names preserved verbatim),
  writes it to the subprocess's stdin, then closes stdin (EOF). The subprocess reads
  JSON from stdin. The JSON payload is bounded to 1 MB. Exit/error semantics are
  unchanged (exit code 0 = success).

## Configuration Constants

```csharp
// SimpleAssistantService
private const int MaxAgentTurns = 50;
private const string MaxTurnsStopReason = "max_turns_exceeded";

// HeadlessAgentRunner
var maxTurns = config.Limits.MaxIterations > 0 ? config.Limits.MaxIterations : 10;
var timeoutSeconds = config.Limits.TimeoutSeconds > 0 ? config.Limits.TimeoutSeconds : 300;
```

## Error Handling

- A failed tool returns an error `ToolResult` (isError = true) carrying the error
  message/data; the model sees it and can react. `UiUpdatingToolExecutor` prefers
  `ErrorMessage` over `Message` when summarizing failures for the feed.
- An exception inside `ToolAdapter.ExecuteAsync` is caught and serialized as an error
  `ToolResult` rather than aborting the turn.
- An exception in `ProcessMessageAsync` clears the indicator, writes a crash log
  (`CrashLog.Write`), renders an error line in the feed, and returns an error string.
- `_runningTools` is a `ConcurrentDictionary` because the `ToolCalled` callback runs on
  the agent's background thread while the end-of-turn loop reads/removes entries.

## Key Design Decisions

1. **The engine owns the loop.** The CLI no longer runs a hand-rolled iteration loop;
   `SimpleAgent` does. The service is an adapter + UI layer around it.
2. **Adapter boundary.** Andy.Tools tools are adapted to the engine's tool interface
   (`ToolAdapter`), and results are serialized back via `Andy.Model.ToolResult`. The two
   tool models stay decoupled.
3. **Permission gate as consent authority.** Capability flags are granted so the gate is
   the single decision point; headless is fail-closed, interactive prompts.
4. **Safe parameter repair.** Only exact + curated aliases on the dispatch path, so the
   model gets latitude with names without risking mis-routed calls.
5. **Loop guard.** Repeated identical calls are short-circuited with guidance rather than
   re-run.

## Related Documentation

- [Headless Runtime](./headless-runtime.md)
- [Event Stream](./event-stream.md)
