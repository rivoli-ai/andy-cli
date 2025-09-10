# Cumulative Output Limiting Solution

## Problem
When users ask follow-up questions like "what are the files underneath?", the LLM might make 6+ tool calls that together exceed context limits, causing HTTP 400 errors.

## Solution: Cumulative Output Tracking

### Components

#### 1. Very Aggressive Individual Limits
Updated `ToolOutputLimits` with much smaller limits:
- `list_directory`: 800 chars (was 1500)
- `read_file`: 1500 chars (was 3000)  
- `search_*`: 1000 chars
- Default: 1000 chars

#### 2. CumulativeOutputTracker
New service that tracks total output across all tools in a conversation turn:
- **Max cumulative**: 6000 chars total
- **Per-tool when multiple**: 800 chars max
- **Dynamic adjustment**: Reduces limits as budget is consumed
- **Minimal fallback**: 100 chars when budget exhausted

#### 3. Integration in AiConversationService
- Resets tracker at start of each user message
- Applies cumulative limits before adding to context
- Records each tool's output to track total usage

### How It Works

When processing "what are the files underneath?" with 6 tool calls:

1. **Tool 1** (`list_directory`): 
   - Base limit: 800 chars
   - Cumulative allows: 800 chars
   - Output: 800 chars
   - Total used: 800/6000

2. **Tool 2** (`list_directory`):
   - Base limit: 800 chars
   - Multiple tools detected → limit: 800 chars
   - Output: 800 chars
   - Total used: 1600/6000

3. **Tools 3-6**:
   - Each gets max 800 chars (multiple tool limit)
   - By tool 6: approaching 6000 char total
   - Last tools get minimal output if budget exhausted

### Key Features

- **Prevents context overflow**: Hard stop at 6000 chars cumulative
- **Fair distribution**: Multiple tools share budget equally
- **Graceful degradation**: Later tools get less space but still function
- **Per-message reset**: Each user message starts fresh

### Testing

Comprehensive test suite verifies:
- Individual tool limits work
- Cumulative tracking prevents overflow
- Multiple tool scenarios stay within limits
- Reset functionality works correctly

### Result

With this solution:
- Single tool queries work well (up to limit)
- Multiple tool queries (6+ calls) stay within total budget
- HTTP 400 errors are eliminated
- LLM gets focused, manageable content

## Configuration

To adjust limits, modify:
- `ToolOutputLimits.Limits` - Per-tool base limits
- `CumulativeOutputTracker.MaxCumulativeOutput` - Total budget (6000)
- `CumulativeOutputTracker.PerToolLimitMultiple` - Multi-tool limit (800)