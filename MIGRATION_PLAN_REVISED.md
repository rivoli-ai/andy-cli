# Andy-CLI Migration to Andy.Engine - Revised Plan

**Date:** 2025-10-04
**Status:** Ready for execution

## Executive Summary

After analyzing both the andy-cli codebase and the andy-engine library (v1.0.0-alpha.1), this plan outlines a clean migration strategy that:

1. Replaces `Andy.Model.Orchestration.Assistant` with `Andy.Engine.Interactive.InteractiveAgent`
2. Keeps CLI-specific tools in andy-cli (they don't belong in the engine)
3. Transforms integration tests into andy-engine benchmark scenarios
4. Removes obsolete orchestration code

## Key Findings

### Andy.Engine Capabilities (v1.0.0-alpha.1)

**Good news**: Andy.Engine now has an `Interactive` namespace that provides:
- `InteractiveAgent` - Wraps the core Agent for conversation-style interactions
- `ConversationManager` - Manages multi-turn conversation history
- `ProcessMessageAsync()` - Single-message processing (suitable for CLI)

**Architecture**:
- Core: `Agent` (goal-oriented, uses Planner → Executor → Critic loop)
- Interactive: `InteractiveAgent` (converts messages to goals, manages conversation)
- The Interactive layer solves the architectural mismatch identified in the original migration findings!

### Current Andy-CLI Architecture

**What we have**:
```
AssistantService.cs
├── Uses: Andy.Model.Orchestration.Assistant
├── Uses: Andy.Model.Orchestration.Conversation
└── Adapters: ToolRegistryAdapter, ToolAdapter

Three CLI-specific tools:
├── CodeIndexTool (semantic code search)
├── BashCommandTool (bash command capture for display)
└── CreateDirectoryTool (directory creation)

Integration Tests:
├── MultiTurnConversationTest
├── ToolChainIntegrationTests
├── ComplexPromptScenarioTests
└── Various other integration tests
```

## Migration Strategy

### Phase 1: Replace AssistantService with InteractiveAgent

**Objective**: Migrate from `Andy.Model.Orchestration` to `Andy.Engine.Interactive`

#### Changes Required:

**1. Update AssistantService.cs**

Replace:
```csharp
using Andy.Model.Orchestration;
private readonly Assistant _assistant;
private readonly Conversation _conversation;
```

With:
```csharp
using Andy.Engine;
using Andy.Engine.Interactive;
using Andy.Engine.Contracts;
private readonly InteractiveAgent _interactiveAgent;
```

**2. Update Initialization (Program.cs or ServiceConfiguration)**

Replace:
```csharp
var conversation = new Conversation { Id = Guid.NewGuid().ToString() };
var assistant = new Assistant(conversation, toolRegistry, llmProvider);
```

With:
```csharp
// Build core agent
var agent = AgentBuilder.Create()
    .WithLlmProvider(llmProvider)
    .WithToolRegistry(toolRegistry)
    .WithDefaults()
    .Build();

// Wrap with interactive layer
var interactiveAgent = InteractiveAgentBuilder.Create(agent)
    .WithOptions(new InteractiveAgentOptions
    {
        DefaultBudget = new Budget(MaxTurns: 10, MaxWallClock: TimeSpan.FromMinutes(5)),
        ConversationOptions = new ConversationOptions
        {
            MaxHistoryTurns = 100,
            SummaryTurnCount = 5
        }
    })
    .Build();
```

**3. Update Event Handling**

Map events from old to new:
- `Assistant.TurnStarted` → `Agent.TurnStarted`
- `Assistant.TurnCompleted` → `Agent.TurnCompleted`
- `Assistant.LlmRequestStarted` → (not directly available, may need to wire through planner)
- `Assistant.ToolCallStarted` → `Agent.ToolCalled`

**4. Update Message Processing**

Replace:
```csharp
await _assistant.RunTurnAsync(userMessage, cancellationToken);
```

With:
```csharp
var result = await _interactiveAgent.ProcessMessageAsync(userMessage, cancellationToken);
```

### Phase 2: Keep CLI-Specific Tools in Andy-CLI

**Decision**: The three tools (CodeIndexTool, BashCommandTool, CreateDirectoryTool) are CLI-specific and should **remain in andy-cli**.

**Rationale**:
- `CodeIndexTool` - Uses Roslyn for semantic code indexing, CLI-specific feature
- `BashCommandTool` - Captures bash commands for CLI display, not actual execution
- `CreateDirectoryTool` - Duplicates functionality from Andy.Tools.Library, can be removed or kept as CLI-specific wrapper

**Action Items**:
- [ ] Keep CodeIndexTool in andy-cli
- [ ] Keep BashCommandTool in andy-cli
- [ ] Evaluate CreateDirectoryTool - may be redundant with Andy.Tools.Library.FileSystem.CreateDirectoryTool
- [ ] No migration of tools to andy-engine needed

### Phase 3: Remove Obsolete Code

**Files/Classes to Remove**:
1. `/Services/Adapters/ToolAdapter.cs` - ToolAdapter and ToolRegistryAdapter classes
   - Andy.Engine integrates directly with Andy.Tools
   - No adapter layer needed

2. Remove unused `Andy.Model.Orchestration` imports throughout codebase

3. Update test mocks that reference old Assistant/Conversation classes

**Dependencies to Update**:
- Remove explicit `Andy.Model` dependency if only used for Orchestration
- Keep `Andy.Model` if used for other shared models
- Ensure `Andy.Engine` project reference is in place (already exists: line 27 of Andy.Cli.csproj)

### Phase 4: Transform Integration Tests to Benchmarks

**Current Tests** (in andy-cli/tests):
- `MultiTurnConversationTest` - Tests multi-turn conversation handling
- `ToolChainIntegrationTests` - Tests tool execution chains
- `ComplexPromptScenarioTests` - Tests complex multi-step scenarios

**Migration Strategy**:

**Option A: Keep CLI Tests in Andy-CLI** (Recommended)
- These tests validate CLI functionality, not engine behavior
- They should remain in andy-cli/tests
- Update them to work with InteractiveAgent instead of Assistant

**Option B: Create Equivalent Benchmarks in Andy-Engine**
- Create new benchmark scenarios in andy-engine/Andy.Benchmarks
- Follow the structure defined in `docs/benchmarking-system.md`
- Examples:
  - Multi-turn conversation scenarios
  - Tool chain execution scenarios
  - Complex task decomposition scenarios

**Recommendation**: Do both!
1. Keep and update the integration tests in andy-cli (they test CLI behavior)
2. Create similar benchmark scenarios in andy-engine (for regression testing the engine)

**Benchmark Examples to Create** (in andy-engine):

```json
{
  "id": "multi-turn-conversation-001",
  "category": "conversation",
  "description": "Multi-turn conversation with tool execution",
  "context": {
    "prompts": [
      "List files in the current directory",
      "Read the README.md file",
      "Summarize what this project does"
    ]
  },
  "expected_tools": [
    {"type": "list_directory"},
    {"type": "read_file", "path_pattern": "**/README.md"}
  ],
  "validation": {
    "conversation_flow": {
      "min_turns": 3,
      "max_turns": 10,
      "must_maintain_context": true
    }
  }
}
```

## Implementation Checklist

### Phase 1: Core Migration
- [ ] Update AssistantService to use InteractiveAgent
- [ ] Update Program.cs to build Agent and InteractiveAgent
- [ ] Map all event subscriptions from Assistant to Agent
- [ ] Update message processing to use ProcessMessageAsync
- [ ] Test basic conversation flow works

### Phase 2: Tool Management
- [ ] Verify CodeIndexTool still works with new engine
- [ ] Verify BashCommandTool still works with new engine
- [ ] Decide on CreateDirectoryTool (remove or keep)
- [ ] Update tool registration to work with Andy.Engine

### Phase 3: Cleanup
- [ ] Remove ToolAdapter.cs and ToolRegistryAdapter.cs
- [ ] Remove Andy.Model.Orchestration imports
- [ ] Remove obsolete assistant-related code
- [ ] Clean up any lingering references

### Phase 4: Testing
- [ ] Update integration tests to use InteractiveAgent
- [ ] Run all tests: `dotnet test`
- [ ] Fix any broken tests
- [ ] Verify CLI functionality manually
- [ ] Create benchmark scenarios in andy-engine (optional but recommended)

### Phase 5: Documentation
- [ ] Update README.md with new architecture
- [ ] Update code comments
- [ ] Document breaking changes (if any)
- [ ] Archive old MIGRATION_PLAN.md and MIGRATION_FINDINGS.md

## Risk Assessment

### Low Risk
- ✅ InteractiveAgent provides similar API to Assistant
- ✅ Event model is compatible
- ✅ Andy.Engine already referenced in project

### Medium Risk
- ⚠️ Event mapping may need adjustments (some events may not be directly available)
- ⚠️ Performance characteristics may differ (Planner → Executor → Critic overhead)
- ⚠️ Error handling patterns may differ

### Mitigation Strategies
1. Keep old code commented out initially during migration
2. Test thoroughly with real LLM interactions
3. Monitor performance and adjust Budget/Options as needed
4. Add logging to track agent decision-making process

## Success Criteria

- [ ] All tests pass with InteractiveAgent
- [ ] CLI functions normally with new engine
- [ ] Tool execution works correctly
- [ ] Multi-turn conversations work
- [ ] Events are properly wired up to UI
- [ ] No dependency on Andy.Model.Orchestration
- [ ] Code is cleaner without adapter layer
- [ ] Performance is acceptable (within 20% of previous)

## Rollback Plan

If migration fails:
1. Revert to git commit before migration
2. Keep using Andy.Model.Orchestration.Assistant
3. File issues on andy-engine repo for missing features
4. Consider hybrid approach (both systems) temporarily

## Timeline Estimate

- Phase 1 (Core Migration): 2-4 hours
- Phase 2 (Tool Management): 1 hour
- Phase 3 (Cleanup): 1 hour
- Phase 4 (Testing): 2-3 hours
- Phase 5 (Documentation): 1 hour

**Total: 7-11 hours** (1-2 days)

## Notes

- The discovery of InteractiveAgent changes the migration from "problematic" to "straightforward"
- This is a much cleaner architecture than maintaining our own orchestration layer
- Andy.Engine is actively developed, we'll benefit from future improvements
- The CLI-specific tools staying in andy-cli is the correct architectural decision
