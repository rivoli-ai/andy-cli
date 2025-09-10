using Xunit;
using Andy.Cli.Services;

namespace Andy.Cli.Tests.Services;

public class CumulativeOutputTrackerTests
{
    [Fact]
    public void GetAdjustedLimit_ReturnsFullLimit_WhenNothingUsed()
    {
        // Arrange
        var tracker = new CumulativeOutputTracker();
        
        // Act
        var limit = tracker.GetAdjustedLimit("read_file", 1500);
        
        // Assert
        Assert.Equal(1500, limit);
    }
    
    [Fact]
    public void GetAdjustedLimit_ReducesLimit_WhenApproachingMax()
    {
        // Arrange
        var tracker = new CumulativeOutputTracker();
        // Record 5000 chars already used
        tracker.RecordOutput("tool1", 5000);
        
        // Act
        var limit = tracker.GetAdjustedLimit("read_file", 1500);
        
        // Assert
        // Should return remaining budget (6000 - 5000 = 1000)
        Assert.Equal(1000, limit);
    }
    
    [Fact]
    public void GetAdjustedLimit_ReturnsMinimal_WhenBudgetExceeded()
    {
        // Arrange
        var tracker = new CumulativeOutputTracker();
        // Record 6500 chars already used (exceeds 6000 limit)
        tracker.RecordOutput("tool1", 6500);
        
        // Act
        var limit = tracker.GetAdjustedLimit("read_file", 1500);
        
        // Assert
        // Should return minimal limit
        Assert.Equal(100, limit);
    }
    
    [Fact]
    public void GetAdjustedLimit_UsesStricterLimit_ForMultipleTools()
    {
        // Arrange
        var tracker = new CumulativeOutputTracker();
        tracker.RecordOutput("tool1", 500);
        tracker.RecordOutput("tool2", 500);
        
        // Act
        var limit = tracker.GetAdjustedLimit("tool3", 1500);
        
        // Assert
        // With multiple tools, should use stricter per-tool limit (800)
        Assert.Equal(800, limit);
    }
    
    [Fact]
    public void Reset_ClearsAllTracking()
    {
        // Arrange
        var tracker = new CumulativeOutputTracker();
        tracker.RecordOutput("tool1", 3000);
        tracker.RecordOutput("tool2", 2000);
        
        // Act
        tracker.Reset();
        var limit = tracker.GetAdjustedLimit("tool3", 1500);
        
        // Assert
        // After reset, should get full limit again
        Assert.Equal(1500, limit);
    }
    
    [Fact]
    public void GetStats_ReturnsCorrectStatistics()
    {
        // Arrange
        var tracker = new CumulativeOutputTracker();
        tracker.RecordOutput("tool1", 2000);
        tracker.RecordOutput("tool2", 3000);
        
        // Act
        var (totalChars, toolCount, nearLimit) = tracker.GetStats();
        
        // Assert
        Assert.Equal(5000, totalChars);
        Assert.Equal(2, toolCount);
        Assert.True(nearLimit); // 5000 > 4800 (80% of 6000)
    }
    
    [Fact]
    public void SimulateMultipleToolCalls_StaysWithinLimits()
    {
        // Simulate the "what are the files underneath?" scenario
        // where multiple list_directory calls might happen
        
        // Arrange
        var tracker = new CumulativeOutputTracker();
        var totalOutput = 0;
        
        // Simulate 6 tool calls (as mentioned in the error)
        for (int i = 0; i < 6; i++)
        {
            var baseLimit = 800; // list_directory limit
            var adjustedLimit = tracker.GetAdjustedLimit($"list_directory_{i}", baseLimit);
            
            // Record output up to the adjusted limit
            tracker.RecordOutput($"list_directory_{i}", adjustedLimit);
            totalOutput += adjustedLimit;
        }
        
        // Assert
        // Total output should not exceed 6000 chars
        Assert.True(totalOutput <= 6000, $"Total output {totalOutput} exceeds 6000 char limit");
        
        // Get final stats
        var (total, count, nearLimit) = tracker.GetStats();
        Assert.True(total <= 6000);
        Assert.Equal(6, count);
    }
}