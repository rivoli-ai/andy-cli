using System;
using System.Collections.Generic;

namespace Andy.Cli.Services;

/// <summary>
/// Tracks cumulative tool output size within a conversation turn
/// to prevent multiple tool calls from exceeding context limits
/// </summary>
public class CumulativeOutputTracker
{
    private int _totalOutputChars = 0;
    private readonly List<string> _toolsExecuted = new();
    
    // Maximum total output across all tools in one turn
    private const int MaxCumulativeOutput = 6000;
    
    // Per-tool limit when multiple tools are called
    private const int PerToolLimitMultiple = 800;
    
    /// <summary>
    /// Get the adjusted limit for a tool based on cumulative usage
    /// </summary>
    public int GetAdjustedLimit(string toolId, int baseLimit)
    {
        // If we're approaching the cumulative limit, reduce individual limits
        var remainingBudget = MaxCumulativeOutput - _totalOutputChars;
        
        if (remainingBudget <= 0)
        {
            // No budget left
            return 100; // Minimal output only
        }
        
        // If multiple tools have been called, use stricter limits
        if (_toolsExecuted.Count >= 2)
        {
            return Math.Min(PerToolLimitMultiple, remainingBudget);
        }
        
        // Otherwise use the smaller of base limit or remaining budget
        return Math.Min(baseLimit, remainingBudget);
    }
    
    /// <summary>
    /// Record that output was generated for a tool
    /// </summary>
    public void RecordOutput(string toolId, int outputLength)
    {
        _totalOutputChars += outputLength;
        if (!_toolsExecuted.Contains(toolId))
        {
            _toolsExecuted.Add(toolId);
        }
        
        // Log if we're getting close to limits
        if (_totalOutputChars > MaxCumulativeOutput * 0.8)
        {
            System.Diagnostics.Debug.WriteLine($"[CumulativeTracker] WARNING: Approaching limit - {_totalOutputChars}/{MaxCumulativeOutput} chars used across {_toolsExecuted.Count} tools");
        }
    }
    
    /// <summary>
    /// Reset the tracker for a new conversation turn
    /// </summary>
    public void Reset()
    {
        _totalOutputChars = 0;
        _toolsExecuted.Clear();
    }
    
    /// <summary>
    /// Get current usage statistics
    /// </summary>
    public (int totalChars, int toolCount, bool nearLimit) GetStats()
    {
        return (_totalOutputChars, _toolsExecuted.Count, _totalOutputChars > MaxCumulativeOutput * 0.8);
    }
}