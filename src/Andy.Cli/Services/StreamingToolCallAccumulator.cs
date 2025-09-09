using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Andy.Llm.Models;

namespace Andy.Cli.Services;

/// <summary>
/// Represents a tool call being accumulated during streaming
/// </summary>
public class AccumulatedToolCall
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public StringBuilder Arguments { get; set; } = new StringBuilder();
    public bool IsComplete { get; set; }
    public int ChunkCount { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Check if this accumulated call has enough data to be considered valid
    /// </summary>
    public bool HasMinimumData => !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// Get the accumulated arguments as a string
    /// </summary>
    public string GetArgumentsString() => Arguments.ToString();
}

/// <summary>
/// Represents a chunk of streaming response data
/// </summary>
public class StreamChunk
{
    public string? Content { get; set; }
    public int? ToolCallIndex { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolCallName { get; set; }
    public string? ToolCallArguments { get; set; }
    public bool IsFinished { get; set; }
    public string? FinishReason { get; set; }
}

/// <summary>
/// Accumulates partial tool calls from streaming responses
/// Based on qwen-code's streaming tool call pattern
/// </summary>
public class StreamingToolCallAccumulator
{
    private readonly Dictionary<int, AccumulatedToolCall> _streamingCalls;
    private readonly IJsonRepairService _jsonRepair;
    private readonly ILogger<StreamingToolCallAccumulator>? _logger;
    private readonly object _lock = new object();

    public StreamingToolCallAccumulator(
        IJsonRepairService jsonRepair,
        ILogger<StreamingToolCallAccumulator>? logger = null)
    {
        _streamingCalls = new Dictionary<int, AccumulatedToolCall>();
        _jsonRepair = jsonRepair;
        _logger = logger;
    }

    /// <summary>
    /// Accumulate a streaming chunk
    /// </summary>
    public void AccumulateChunk(StreamChunk chunk)
    {
        if (chunk == null)
            return;

        lock (_lock)
        {
            // If this chunk contains tool call data
            if (chunk.ToolCallIndex.HasValue)
            {
                var index = chunk.ToolCallIndex.Value;

                // Get or create the accumulated call for this index
                if (!_streamingCalls.TryGetValue(index, out var accumulatedCall))
                {
                    accumulatedCall = new AccumulatedToolCall();
                    _streamingCalls[index] = accumulatedCall;
                    _logger?.LogDebug("Started accumulating tool call at index {Index}", index);
                }

                // Update accumulated data
                if (!string.IsNullOrEmpty(chunk.ToolCallId))
                {
                    accumulatedCall.Id = chunk.ToolCallId;
                }

                if (!string.IsNullOrEmpty(chunk.ToolCallName))
                {
                    // If we get a new function name and already have arguments,
                    // this might be a new call with the same index (shouldn't happen but be defensive)
                    if (!string.IsNullOrEmpty(accumulatedCall.Name) &&
                        accumulatedCall.Name != chunk.ToolCallName &&
                        accumulatedCall.Arguments.Length > 0)
                    {
                        _logger?.LogWarning("Received new function name '{NewName}' but already accumulating '{OldName}' at index {Index}",
                            chunk.ToolCallName, accumulatedCall.Name, index);

                        // Check if current arguments form complete JSON
                        if (_jsonRepair.IsCompleteJson(accumulatedCall.GetArgumentsString()))
                        {
                            // Mark current as complete and start a new one
                            accumulatedCall.IsComplete = true;

                            // Create new accumulator for the new function
                            accumulatedCall = new AccumulatedToolCall
                            {
                                Name = chunk.ToolCallName
                            };
                            _streamingCalls[index + 1000] = accumulatedCall; // Use offset to avoid collision
                        }
                        else
                        {
                            // Replace with new function
                            accumulatedCall.Arguments.Clear();
                        }
                    }

                    accumulatedCall.Name = chunk.ToolCallName;
                }

                if (!string.IsNullOrEmpty(chunk.ToolCallArguments))
                {
                    AppendArguments(accumulatedCall, chunk.ToolCallArguments);
                }

                accumulatedCall.ChunkCount++;
            }

            // If this chunk indicates streaming is finished
            if (chunk.IsFinished)
            {
                _logger?.LogDebug("Stream finished with reason: {Reason}", chunk.FinishReason);

                // Mark all accumulated calls as complete
                foreach (var call in _streamingCalls.Values)
                {
                    call.IsComplete = true;
                }
            }
        }
    }

    /// <summary>
    /// Append arguments to an accumulated call, handling potential JSON boundaries
    /// </summary>
    private void AppendArguments(AccumulatedToolCall call, string newArguments)
    {
        var currentArgs = call.GetArgumentsString();

        // Check if we already have complete JSON and new arguments start a new object
        if (!string.IsNullOrWhiteSpace(currentArgs) &&
            newArguments.TrimStart().StartsWith('{'))
        {
            if (_jsonRepair.IsCompleteJson(currentArgs))
            {
                _logger?.LogDebug("Current arguments form complete JSON, but received new object. Possible new tool call.");
                // This might indicate a parsing issue or a new tool call
                // For now, we'll continue appending but log the issue
            }
        }

        call.Arguments.Append(newArguments);
    }

    /// <summary>
    /// Get all completed tool calls and remove them from accumulator
    /// </summary>
    public List<ModelToolCall> GetCompletedCalls()
    {
        lock (_lock)
        {
            var completedCalls = new List<ModelToolCall>();
            var keysToRemove = new List<int>();

            foreach (var kvp in _streamingCalls)
            {
                var call = kvp.Value;

                if (call.IsComplete && call.HasMinimumData)
                {
                    var toolCall = ConvertToToolCall(call);
                    if (toolCall != null)
                    {
                        completedCalls.Add(toolCall);
                        keysToRemove.Add(kvp.Key);

                        _logger?.LogDebug("Completed tool call: {Name} with {ArgLength} chars of arguments",
                            call.Name, call.Arguments.Length);
                    }
                }
            }

            // Remove completed calls
            foreach (var key in keysToRemove)
            {
                _streamingCalls.Remove(key);
            }

            return completedCalls;
        }
    }

    /// <summary>
    /// Get all accumulated calls (complete or not) without removing them
    /// </summary>
    public List<ModelToolCall> GetAllCalls(bool includeIncomplete = false)
    {
        lock (_lock)
        {
            var allCalls = new List<ModelToolCall>();

            foreach (var call in _streamingCalls.Values)
            {
                if (call.HasMinimumData && (call.IsComplete || includeIncomplete))
                {
                    var toolCall = ConvertToToolCall(call);
                    if (toolCall != null)
                    {
                        allCalls.Add(toolCall);
                    }
                }
            }

            return allCalls;
        }
    }

    /// <summary>
    /// Convert an accumulated call to a ModelToolCall
    /// </summary>
    private ModelToolCall? ConvertToToolCall(AccumulatedToolCall accumulated)
    {
        if (string.IsNullOrWhiteSpace(accumulated.Name))
            return null;

        var toolCall = new ModelToolCall
        {
            ToolId = accumulated.Name,
            Parameters = new Dictionary<string, object?>()
        };

        // Store the call ID in parameters if needed for correlation
        if (!string.IsNullOrWhiteSpace(accumulated.Id))
        {
            toolCall.Parameters["_callId"] = accumulated.Id;
        }

        // Parse the arguments
        var argsString = accumulated.GetArgumentsString();
        if (!string.IsNullOrWhiteSpace(argsString))
        {
            // Use JsonRepairService to handle potentially malformed JSON
            var parsedArgs = _jsonRepair.SafeParse<Dictionary<string, object?>>(
                argsString,
                new Dictionary<string, object?>());

            if (parsedArgs != null)
            {
                toolCall.Parameters = parsedArgs;
            }
            else
            {
                _logger?.LogWarning("Failed to parse tool call arguments for {Name}. Raw: {Args}",
                    accumulated.Name, argsString);

                // Store raw arguments as a single parameter if parsing fails
                toolCall.Parameters["_raw_arguments"] = argsString;
            }
        }

        return toolCall;
    }

    /// <summary>
    /// Generate a unique tool call ID
    /// </summary>
    private string GenerateToolCallId()
    {
        return $"call_{DateTime.UtcNow.Ticks}_{Guid.NewGuid():N}".Substring(0, 32);
    }

    /// <summary>
    /// Clear all accumulated calls
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _logger?.LogDebug("Clearing {Count} accumulated tool calls", _streamingCalls.Count);
            _streamingCalls.Clear();
        }
    }

    /// <summary>
    /// Get statistics about current accumulation state
    /// </summary>
    public AccumulatorStats GetStats()
    {
        lock (_lock)
        {
            return new AccumulatorStats
            {
                TotalCalls = _streamingCalls.Count,
                CompleteCalls = _streamingCalls.Count(x => x.Value.IsComplete),
                IncompleteCalls = _streamingCalls.Count(x => !x.Value.IsComplete),
                TotalChunks = _streamingCalls.Sum(x => x.Value.ChunkCount),
                OldestCallAge = _streamingCalls.Any()
                    ? DateTime.UtcNow - _streamingCalls.Values.Min(x => x.StartTime)
                    : TimeSpan.Zero
            };
        }
    }
}

/// <summary>
/// Statistics about the accumulator state
/// </summary>
public class AccumulatorStats
{
    public int TotalCalls { get; set; }
    public int CompleteCalls { get; set; }
    public int IncompleteCalls { get; set; }
    public int TotalChunks { get; set; }
    public TimeSpan OldestCallAge { get; set; }
}