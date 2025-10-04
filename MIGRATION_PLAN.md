# Andy-CLI to Andy.Engine Migration Plan

## Overview

This document outlines the migration from using `Andy.Model.Orchestration` to the new `Andy.Engine` library as the core engine for andy-cli.

## Current Architecture

### Dependencies
- `Andy.Model` (2025.9.18-rc.3) - Contains `Andy.Model.Orchestration.Assistant` and `Conversation`
- `Andy.Tools` (2025.9.5-rc.15) - Tool registry and execution
- `Andy.Llm` (2025.9.19-rc.15) - LLM provider abstractions
- `Andy.Context` (2025.9.21-rc.3) - Context management
- `Andy.Tui` (2025.8.26-rc.31) - Terminal UI

### Key Classes to Replace
- `Andy.Model.Orchestration.Assistant` → `Andy.Engine.Agent`
- `Andy.Model.Orchestration.Conversation` → `Andy.Engine` state management
- `Services/Adapters/ToolAdapter.cs` → Use Andy.Engine's tool integration directly

### CLI-Specific Components (Keep in andy-cli)
- `/Tools/CodeIndexTool.cs` - Code indexing for CLI
- `/Tools/BashCommandTool.cs` - Bash command capture for CLI
- `/Tools/CreateDirectoryTool.cs` - Directory creation for CLI
- `/Widgets/*` - All TUI widgets
- `/Commands/*` - CLI commands
- `/Services/ContentPipeline/*` - Content rendering pipeline

## New Architecture with Andy.Engine

### Package Changes
1. Add `Andy.Engine` (1.0.0-alpha.1) package reference
2. Update `Andy.Context` to latest version (2025.9.23-rc.4) to match andy-engine
3. Keep other Andy packages at compatible versions

### Code Changes

#### 1. Update AssistantService.cs
Replace:
```csharp
using Andy.Model.Orchestration;
private readonly Assistant _assistant;
private readonly Conversation _conversation;
```

With:
```csharp
using Andy.Engine;
using Andy.Engine.Contracts;
private readonly Agent _agent;
private AgentState _currentState;
```

#### 2. Update Program.cs
Replace Assistant/Conversation initialization with AgentBuilder:
```csharp
var agent = AgentBuilder.Create()
    .WithDefaults(llmProvider, toolRegistry, toolExecutor)
    .WithPlannerOptions(new PlannerOptions { Temperature = 0.0 })
    .Build();
```

#### 3. Remove Adapter Classes
- Delete `/Services/Adapters/ToolAdapter.cs` (ToolAdapter and ToolRegistryAdapter)
- Andy.Engine integrates with Andy.Tools directly

### Event Mapping

Current events in AssistantService to Andy.Engine events:
- `TurnStarted` → `TurnStarted`
- `TurnCompleted` → `TurnCompleted`
- `LlmRequestStarted` → Wire up from Agent events
- `LlmResponseReceived` → Wire up from Agent events
- `ToolCallStarted` → `ToolCalled`
- `ToolCallCompleted` → `ToolCalled`

## Test Migration Strategy

### Keep in andy-cli/tests
All integration tests stay as they test the CLI functionality:
- `/Integration/*` - All integration tests
- `/Services/*` - Service-level tests
- `/Commands/*` - Command tests

### Consider Moving to andy-engine/tests
None - andy-cli tests are CLI-specific and should remain in andy-cli.

### New Tests for andy-engine
Based on `docs/benchmarking-system.md`, add to andy-engine:
- Tool invocation scenarios
- Multi-turn conversation scenarios
- Error handling and recovery scenarios
- State management scenarios

## Migration Steps

### Phase 1: Setup (Current)
- [x] Analyze current dependencies and architecture
- [x] Review andy-engine documentation
- [x] Identify components to migrate vs keep
- [x] Create migration plan

### Phase 2: Package Updates
- [ ] Add Andy.Engine package reference to andy-cli
- [ ] Update Andy.Context to match andy-engine version
- [ ] Verify package compatibility
- [ ] Run `dotnet restore` and fix any conflicts

### Phase 3: Code Migration
- [ ] Update AssistantService to use Agent instead of Assistant
- [ ] Replace Conversation with Andy.Engine state management
- [ ] Update Program.cs initialization code
- [ ] Remove ToolAdapter and ToolRegistryAdapter classes
- [ ] Update event subscriptions to use Andy.Engine events

### Phase 4: Testing & Validation
- [ ] Run existing tests: `dotnet test`
- [ ] Fix any broken tests
- [ ] Verify CLI functionality manually
- [ ] Test tool execution through new engine
- [ ] Verify multi-turn conversations work

### Phase 5: Cleanup
- [ ] Remove Andy.Model.Orchestration usings
- [ ] Remove deprecated adapter classes
- [ ] Update documentation
- [ ] Commit changes with detailed message

## Risks & Mitigation

### Risk: Breaking Changes in Andy.Engine API
**Mitigation**: Andy.Engine follows similar patterns to Andy.Model.Orchestration. Events and core concepts are similar.

### Risk: Tool Integration Differences
**Mitigation**: Andy.Engine uses Andy.Tools directly, simplifying the integration. No adapter needed.

### Risk: State Management Changes
**Mitigation**: Review Andy.Engine state management and map conversation state properly.

## Success Criteria

- [ ] All tests pass
- [ ] CLI functions normally with new engine
- [ ] Tool execution works correctly
- [ ] Multi-turn conversations work
- [ ] Events are properly wired up to UI
- [ ] No dependency on Andy.Model.Orchestration
- [ ] Code is cleaner without adapter layer

## Notes

- The CLI-specific tools (CodeIndexTool, BashCommandTool, CreateDirectoryTool) remain in andy-cli
- These are UI/CLI-specific and not part of the core engine
- Andy.Engine provides the agent orchestration, andy-cli provides the UI and CLI-specific tools
- This separation keeps concerns properly divided
