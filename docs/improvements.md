# andy-cli: Conversation, Prompt, and Tool Integration Plan

## Goals
- Never mention internal tools in user-visible output.
- Always ground responses in actual tool outputs (no hallucinations).
- Improve engagement via clarify→plan→act→conclude loop (without revealing tools).

## Scope (andy-cli)
- System prompt hardening and non-leak policy.
- Rendering and sanitization of assistant-visible text.
- Tool result ingestion: raw JSON in LLM context; user-facing feed remains clean.
- Parameter correction and single automatic retry.
- Compiler lifecycle on model switch.
- Integration tests to lock behavior.

## Changes

### System Prompt (no tool leakage)
- Remove “[Tool Results]” and any mention of internal tool protocol.
- Add rules:
  - “Never mention tools, tool names, or tool execution.”
  - “Reference only entities verified by tool results.”
  - “If ambiguous, ask 1–2 clarifying questions, then present a brief plan (no tool mention).”
  - “For multi-step tasks: Plan → Act → Conclude. Keep answers grounded and concise.”

### Assistant Output Sanitization
- Sanitize text before adding to feed:
  - Strip protocol markers (e.g., [Tool Request], embedded tool JSON/XML tags).
  - Remove lines that describe tool usage.
  - Preserve code fences and normal content.

### Tool Result Grounding
- After a function call:
  - Prefer `result.Data` (serialize to JSON, camelCase) as the tool message content (role=tool) in LLM context referencing the original `call_id`.
  - Else use `FullOutput`.
  - Else use `Message`/`ErrorMessage`.
- Keep UI feed’s “tool execution” visualization separate from what is sent back to the model.

### Function Schemas in Requests
- Build function definitions from `ToolRegistration.Metadata` and attach to `LlmRequest`.
- Single-tool-at-a-time execution loop:
  - Wait for tool result before continuing.

### Parameter Correction & Retry
- On validation error (wrong names/values):
  - Apply `ParameterMapper` suggestions and retry once automatically.
  - If second attempt fails, summarize error succinctly and ask for guidance (no tool mention).

### Compiler Lifecycle
- On `UpdateModelInfo`, recreate `LlmResponseCompiler` with current provider/model.
- Normalize provider naming so Qwen-specific parsing is used for Qwen models (even via Cerebras).

### Interaction Depth
- If multi-step or ambiguous:
  - Ask clarifying question(s) and present a short plan (no tool mention).
- After results:
  - Conclude with grounded findings; offer next investigative steps.

## Tests

### Rendering/Privacy
- No-tool-mention test: mock a successful function call, assert user-visible feed contains no tool IDs/markers.
- Sanitization test: content that includes protocol lines is rendered without those lines.

### Grounding
- Directory listing test: feed known JSON result, assert assistant references only those items.
- Result serialization test: confirm context tool message contains the raw JSON, not prose.

### Resilience
- Param correction test: simulate invalid params (e.g., max_depth=0) → single corrected retry → grounded final answer.
- Provider adhesion test: switch model/provider, assert compiler reinit and continued tool-call extraction.

### Streaming
- Streaming function-call test: only act after streaming completion; final answer grounded.

## Rollout & Diagnostics
- Keep `ANDY_DEBUG_RAW` capturing request/response to disk but never display in feed.
- Add an audit step after rendering: if tool-leak phrases are detected, strip and re-render.
- Maintain context compression but always retain latest tool-call + tool-result uncompressed.

## Definition of Done
- Assistant never mentions internal tools in UI output.
- Answers match tool outputs; no invented items.
- One-shot param correction and retry on validation failures.
- Function schemas attached; tool results added as raw JSON tool messages.
- Integration tests above pass; coverage updated.