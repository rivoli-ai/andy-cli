# Andy-CLI to Andy.Engine Migration - Findings and Recommendations

## Current Status

✅ Andy.Engine package reference added to andy-cli
✅ Package versions updated to match andy-engine (Andy.Model 2025.9.20-rc.4, Andy.Context 2025.9.23-rc.4)
✅ Build succeeds with andy-engine reference

## Architectural Mismatch Discovered

### Andy.Model.Orchestration.Assistant (Current)
```csharp
// Turn-based conversation model
var response = await assistant.RunTurnAsync(userMessage, cancellationToken);
// OR
await foreach (var chunk in assistant.RunTurnStreamAsync(userMessage, ct)) { }
```

**Characteristics:**
- **Turn-based**: One user message → One LLM response (with potential tool calls)
- **Synchronous conversation**: User waits for response, then sends next message
- **Event-driven**: Rich events for tool calls, validation, errors
- **Suitable for**: Chat applications, CLI interfaces, interactive sessions

### Andy.Engine.Agent (New)
```csharp
// Goal-oriented autonomous execution
var result = await agent.RunAsync(goal, budget, errorPolicy, cancellationToken);
```

**Characteristics:**
- **Goal-oriented**: Given a goal, runs autonomously until goal is achieved or budget exhausted
- **Multi-turn autonomous**: Executes multiple LLM calls and tool invocations without user interaction
- **Planning-based**: Uses Planner → Executor → Critic loop
- **Suitable for**: Autonomous tasks, batch processing, agent workflows

### Andy.Engine.InteractiveAgent (Wrapper)
```csharp
// Wraps Agent for interactive use
var result = await interactiveAgent.ProcessMessageAsync(userMessage, cancellationToken);
```

**Characteristics:**
- **Converts message to goal**: Each user message becomes an agent goal
- **Runs to completion**: Executes the full agent loop for each message
- **Less interactive**: More autonomous than turn-based chat
- **Suitable for**: Task-oriented interactions (e.g., "deploy the application", "refactor this code")

## The Core Issue

**andy-cli needs:** Turn-based chat where each user message gets ONE LLM response

**andy-engine provides:** Goal-oriented autonomous agents that run until goals are achieved

**Example scenario:**
```
User: "What is 2 + 2?"

With Assistant (current):
- LLM responds: "2 + 2 = 4"
- Done, waits for next user message

With Agent (andy-engine):
- Planner creates goal: "Calculate 2 + 2"
- Executor might call calculator tool
- Critic checks if goal satisfied
- Might take multiple turns to "achieve" the goal
- Over-engineering for a simple question
```

## Options for Migration

### Option 1: Keep Andy.Model.Orchestration.Assistant ❌
**Decision:** Not recommended
- Defeats the purpose of the migration
- Andy.Model may become deprecated
- Missing out on andy-engine improvements

### Option 2: Use Andy.Engine.InteractiveAgent ⚠️
**Decision:** Possible but changes UX significantly
- Each user message becomes a "goal"
- Agent runs autonomously to achieve it
- More suitable for task-oriented interactions
- Less suitable for conversational chat
- **Would require UX redesign**

### Option 3: Build TurnBasedAgent Wrapper ⚠️
```csharp
public class TurnBasedAgent
{
    private readonly Agent _agent;

    public async Task<Message> RunTurnAsync(string userMessage, CancellationToken ct)
    {
        // Create a goal from the message
        var goal = new AgentGoal(userMessage, []);

        // Run with budget of 1 turn
        var budget = new Budget(MaxTurns: 1, MaxWallClock: TimeSpan.FromMinutes(2));

        // Execute
        var result = await _agent.RunAsync(goal, budget, errorPolicy, ct);

        // Extract response from result
        return ExtractMessage(result);
    }
}
```

**Pros:**
- Adapts andy-engine to turn-based chat
- Reuses andy-engine infrastructure

**Cons:**
- Artificial constraint (1-turn budget)
- Misuses the goal-oriented design
- May not work well with planner/critic loop
- Tools might not work as expected in 1-turn mode

### Option 4: Hybrid Approach ✅ RECOMMENDED
**Use Andy.Engine for the right use cases**

Keep the current architecture BUT:
1. Add Andy.Engine-based agents for **task-oriented commands**
2. Keep conversation-based interaction for chat
3. CLI can detect intent and route to appropriate handler

```csharp
// Chat mode (keep current)
if (IsConversationalQuery(userInput))
{
    response = await assistantService.ProcessMessageAsync(userInput);
}
// Task mode (new, uses andy-engine)
else if (IsTaskCommand(userInput))
{
    result = await agentService.ExecuteTaskAsync(userInput);
}
```

**Examples:**
- "What is 2 + 2?" → Chat mode (current Assistant)
- "Refactor all error handling to use Result pattern" → Agent mode (andy-engine)
- "Find all TODO comments and create GitHub issues" → Agent mode (andy-engine)

## Recommended Next Steps

### Immediate (Keep Current Architecture)
1. ✅ Andy.Engine is already referenced (for future use)
2. ✅ Package versions are updated
3. ✅ Build succeeds
4. **Keep using Andy.Model.Orchestration.Assistant for now**
5. **Add agent-based task execution as a new feature**

### Future Enhancement (Add Agent Capabilities)
1. Create `AgentTaskService` using Andy.Engine
2. Add task detection logic to router
3. Implement task-oriented commands:
   - `/task <goal>` - Run autonomous agent for a goal
   - `/refactor <description>` - Code refactoring tasks
   - `/generate <spec>` - Code generation tasks
4. Keep chat mode for conversational interactions

### Long-term (If Andy.Model.Orchestration is deprecated)
If Andy.Model.Orchestration.Assistant is being phased out:
1. Request turn-based API in andy-engine (or contribute it)
2. Or fully redesign andy-cli to be task-oriented
3. Or build robust TurnBasedAgent wrapper

## Conclusion

**Current Recommendation:**
- ✅ Keep andy-cli using Andy.Model.Orchestration.Assistant for chat
- ✅ Add Andy.Engine for agent-based task execution as new capability
- ✅ Create hybrid system that uses the right tool for the job

**Why:**
- Andy.Engine is designed for autonomous agents, not turn-based chat
- Forcing it into turn-based mode would be an architectural mismatch
- A hybrid approach leverages both strengths
- CLI-specific tools stay in andy-cli (correct)
- Migration to pure andy-engine should wait for proper turn-based API

**Deprecated Classes to Remove:**
Since we're keeping the current orchestration for now:
- None yet - wait for official deprecation notice from Andy.Model

**Tools Analysis:**
- ✅ CodeIndexTool, BashCommandTool, CreateDirectoryTool are CLI-specific
- ✅ Correctly stay in andy-cli
- ✅ Do not belong in andy-engine (which is a general agent framework)

## Migration Plan Update

The MIGRATION_PLAN.md should be updated to reflect this hybrid approach rather than a full replacement.
