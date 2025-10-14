# Archived Tests

This directory contains tests from the old architecture that used `Andy.Model.Orchestration.Assistant`.

These tests are preserved for reference but are not actively maintained after the migration to `Andy.Engine.Interactive.InteractiveAgent`.

## Archived on: 2025-10-04

## Reason for Archival

The andy-cli project migrated from:
- **Old:** `Andy.Model.Orchestration.Assistant` with custom adapters
- **New:** `Andy.Engine.Interactive.InteractiveAgent` with direct tool integration

Many tests were tightly coupled to:
- Provider-specific response formats (Qwen, etc.)
- Old API types (`LlmClient`, `LlmResponse`, `StreamChunk`)
- Custom parsing and rendering logic
- Adapter classes (`ToolAdapter`, `ToolRegistryAdapter`)

## Test Categories Archived

### Provider-Specific Tests (Obsolete)
- `Qwen*Test.cs` - Tests for Qwen-specific response parsing
- `ModelResponseInterpreterTests.cs` - Old model response interpretation
- `StreamingToolCallAccumulatorTests.cs` - Old streaming accumulation logic

### Old Architecture Tests (Obsolete)
- `AiConversationServiceTests.cs` - Tests old conversation service
- `ToolAdapterTests.cs` - Tests obsolete adapter layer
- `ToolExecutionServiceTests.cs` - Tests old execution service

### Integration Tests (Need Updating)
- `MultiTurnConversationTest.cs` - **Should be rewritten** for andy-engine
- `ComplexPromptScenarioTests.cs` - **Should be rewritten** for andy-engine
- `ToolChainIntegrationTests.cs` - **Should be rewritten** for andy-engine

## Recommended New Tests

Instead of updating these tests, create new ones:

### 1. Engine Integration Tests
```csharp
// Test InteractiveAgent with FeedView
// Test multi-turn conversations
// Test tool execution flow
```

### 2. Benchmark Scenarios (in andy-engine project)
```json
{
  "id": "cli-multi-turn-001",
  "category": "conversation",
  "description": "Multi-turn conversation with file operations",
  "expected_tools": ["read_file", "write_file"],
  "validation": {
    "min_turns": 3,
    "max_turns": 10
  }
}
```

### 3. CLI-Specific Tests
```csharp
// Test EngineAssistantService initialization
// Test FeedUserInterface implementation
// Test CodeIndexTool integration
```

## Migration Reference

See:
- `/MIGRATION_PLAN_REVISED.md` - Detailed migration plan
- `/MIGRATION_STATUS.md` - Current migration status
- `/MIGRATION_FINDINGS.md` - Original findings and architectural decisions
