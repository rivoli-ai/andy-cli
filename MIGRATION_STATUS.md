# Andy-CLI Migration to Andy.Engine - Status Report

**Date:** 2025-10-04
**Migration Goal:** Replace `Andy.Model.Orchestration.Assistant` with `Andy.Engine.Interactive.InteractiveAgent`

## ✅ Completed Tasks

### Phase 1: Core Migration (COMPLETE)

1. **Created New Services**
   - ✅ `FeedUserInterface.cs` - IUserInterface implementation for FeedView
   - ✅ `EngineAssistantService.cs` - New service using Andy.Engine.InteractiveAgent
   - Both files compile successfully with only nullable warnings

2. **Updated Program.cs**
   - ✅ Replaced all 3 instances of `AssistantService` with `EngineAssistantService`
   - ✅ Removed Andy.Model.Orchestration using directive
   - ✅ Removed fallback Assistant code
   - ✅ Build succeeds with no errors

3. **Removed Obsolete Code**
   - ✅ Renamed `AssistantService.cs` → `AssistantService.cs.old` (backup)
   - ✅ Renamed `ToolAdapter.cs` → `ToolAdapter.cs.old` (backup)
   - ✅ Removed ToolRegistryAdapter usage
   - ✅ Build still succeeds after removal

### Phase 3: Code Cleanup (COMPLETE)

The following obsolete files have been backed up with `.old` extension:
- `src/Andy.Cli/Services/AssistantService.cs.old`
- `src/Andy.Cli/Services/Adapters/ToolAdapter.cs.old`

These can be deleted once testing confirms the migration works.

## 📋 Remaining Tasks

### Phase 2: Tool Migration

**Decision Made:** Keep CLI tools in andy-cli (correct architectural choice)
- ✅ CodeIndexTool - CLI-specific semantic search
- ✅ BashCommandTool - CLI-specific display tool
- ✅ CreateDirectoryTool - May be redundant, evaluate later

**Status:** Tools are compatible with andy-engine's IToolRegistry. No migration needed.

### Phase 4: Test Updates (IN PROGRESS)

**Current Issue:** Many tests fail compilation due to:

1. **API Changes (47+ compilation errors)**
   - Tests reference `Andy.Llm.Models` (obsolete)
   - Tests use old `AiConversationService`, `ToolAdapter`, etc.
   - Tests use old model types: `LlmClient`, `LlmResponse`, `StreamChunk`

2. **Test Files Requiring Updates:**
   ```
   Integration Tests:
   - AiConversationComprehensiveAnswerTest.cs
   - ComplexPromptScenarioTests.cs
   - ContextManagementTest.cs
   - LargeFileHandlingTest.cs
   - ListDirectoryTruncationTest.cs
   - MultiTurnConversationTest.cs
   - PromptBasedToolTests.cs
   - QwenIntegrationTest.cs

   Service Tests:
   - AiConversationServiceTests.cs
   - ModelResponseInterpreterTests.cs
   - QwenFullFlowTest.cs
   - QwenMissingToolCallTest.cs
   - QwenDateTimeTest.cs
   - QwenResponseTest.cs
   - QwenNoRepetitionTest.cs
   - QwenLargeResponseTest.cs
   - QwenSimpleResponseTest.cs
   - SimpleGreetingResponseTests.cs
   - StreamingRepetitionTest.cs
   - ToolCallExtractionTests.cs
   - ToolExecutionServiceTests.cs
   - StreamingToolCallAccumulatorTests.cs

   Adapter Tests:
   - ToolAdapterTests.cs

   Test Helpers:
   - TestResponseHelper.cs
   - ExampleTestUsage.cs
   ```

3. **Test Migration Strategy:**

   **Option A: Update Existing Tests** (Recommended for critical tests)
   - Update integration tests to use `EngineAssistantService`
   - Replace mock LLM responses with andy-engine compatible mocks
   - Focus on: MultiTurnConversationTest, ComplexPromptScenarioTests, ToolChainIntegrationTests

   **Option B: Archive Old Tests, Write New Ones**
   - Move obsolete tests to `/tests/Andy.Cli.Tests/Archived/`
   - Write new tests focused on andy-engine integration
   - Create benchmark scenarios in andy-engine project

   **Option C: Hybrid Approach** (Recommended)
   - Archive provider-specific tests (Qwen*, ModelResponseInterpreter*)
   - Update high-value integration tests
   - Create new andy-engine benchmarks for regression testing

### Phase 5: Documentation Updates (PENDING)

1. **Update README.md**
   - Document andy-engine migration
   - Update architecture diagrams
   - Update local development setup

2. **Update Migration Documents**
   - Archive MIGRATION_PLAN.md and MIGRATION_FINDINGS.md
   - Update this status document to final report

3. **Code Documentation**
   - Add XML comments to EngineAssistantService
   - Add XML comments to FeedUserInterface
   - Document event handling differences

## 🏗️ Architecture Changes

### Before Migration
```
Program.cs
  └─> AssistantService (Andy.Model.Orchestration.Assistant)
       └─> ToolRegistryAdapter
            └─> ToolAdapter
                 └─> Andy.Tools
```

### After Migration
```
Program.cs
  └─> EngineAssistantService (Andy.Engine.Interactive.InteractiveAgent)
       ├─> FeedUserInterface (IUserInterface implementation)
       └─> Andy.Engine.Agent (Planner → Executor → Critic loop)
            └─> Andy.Tools (direct integration, no adapters)
```

**Key Improvements:**
- ✅ No adapter layer needed
- ✅ Cleaner event model
- ✅ Goal-oriented agent architecture
- ✅ Turn-based conversation support (via InteractiveAgent)
- ✅ Better state management

## 🧪 Testing Status

**Build:** ✅ SUCCESS (0 errors, 9 warnings)
**Tests:** ❌ FAILING (47+ compilation errors)

**Next Steps:**
1. Choose test migration strategy (Option C recommended)
2. Archive obsolete provider-specific tests
3. Update core integration tests
4. Create benchmark scenarios in andy-engine
5. Run full test suite
6. Manual testing of CLI functionality

## 📊 Migration Metrics

- **Files Created:** 2
- **Files Updated:** 1 (Program.cs)
- **Files Removed:** 2 (backed up as .old)
- **Build Status:** ✅ Success
- **Test Status:** ⚠️ Needs Update
- **Estimated Remaining Work:** 4-6 hours (tests + documentation)

## 🚀 How to Test the Migration

### Manual Testing
```bash
# Build the project
dotnet build src/Andy.Cli/Andy.Cli.csproj

# Run the CLI (requires API keys)
export CEREBRAS_API_KEY=your_key
dotnet run --project src/Andy.Cli/Andy.Cli.csproj

# Test basic conversation
# Test tool execution
# Test model switching
```

### Automated Testing (After Test Updates)
```bash
# Run updated tests
dotnet test tests/Andy.Cli.Tests/Andy.Cli.Tests.csproj
```

## 📝 Notes

- The migration preserves API compatibility - `EngineAssistantService` has the same public methods as `AssistantService`
- Event subscriptions have been mapped from old to new (TurnStarted, TurnCompleted, ToolCalled, etc.)
- The andy-engine InteractiveAgent provides conversation management out of the box
- Old backup files (.old) can be deleted after successful testing
- CLI tools (CodeIndexTool, BashCommandTool, CreateDirectoryTool) remain in andy-cli

## 🔄 Rollback Plan

If the migration needs to be rolled back:

```bash
# Restore old files
mv src/Andy.Cli/Services/AssistantService.cs.old src/Andy.Cli/Services/AssistantService.cs
mv src/Andy.Cli/Services/Adapters/ToolAdapter.cs.old src/Andy.Cli/Services/Adapters/ToolAdapter.cs

# Revert Program.cs changes
git checkout src/Andy.Cli/Program.cs

# Remove new files
rm src/Andy.Cli/Services/EngineAssistantService.cs
rm src/Andy.Cli/Services/FeedUserInterface.cs

# Rebuild
dotnet build
```

## ✨ Success Criteria

- [x] Build succeeds with no errors
- [ ] Core integration tests pass
- [ ] Manual testing confirms functionality
- [ ] Documentation updated
- [ ] Old code removed (not just backed up)
- [ ] Performance is acceptable (within 20% of previous)

**Overall Progress: 75% Complete**
