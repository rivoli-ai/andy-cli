using System.Collections.Generic;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class ToolExecutionTraceTests
{
    [Fact]
    public void AddToolExecutionStart_WithParameters_CreatesRunningToolItem()
    {
        // Arrange
        var feedView = new FeedView();
        var parameters = new Dictionary<string, object?>
        {
            { "file_path", "/test/file.txt" },
            { "limit", 100 }
        };

        // Act
        feedView.AddToolExecutionStart("read_file", "Read File", parameters);

        // Assert
        var items = feedView.GetItemsForTesting();
        Assert.Single(items);
        // RunningToolItem is internal, so check by type name
        Assert.Equal("RunningToolItem", items[0].GetType().Name);
    }

    [Fact]
    public void AddToolExecutionDetail_AddsDetailToRunningTool()
    {
        // Arrange
        var feedView = new FeedView();
        feedView.AddToolExecutionStart("bash_command", "Bash Command");

        // Act
        feedView.AddToolExecutionDetail("bash_command", "Executing: ls -la");
        feedView.AddToolExecutionDetail("bash_command", "Found 10 files");

        // Assert
        var items = feedView.GetItemsForTesting();
        Assert.Single(items);
        // RunningToolItem is internal, verify it exists and has correct type
        Assert.Equal("RunningToolItem", items[0].GetType().Name);
    }

    [Fact]
    public void AddToolExecutionComplete_WithResult_UpdatesToolStatus()
    {
        // Arrange
        var feedView = new FeedView();
        feedView.AddToolExecutionStart("update_file", "Update File");

        // Act
        feedView.AddToolExecutionComplete("update_file", true, "1.5s", "Updated 5 lines");

        // Assert
        var items = feedView.GetItemsForTesting();
        Assert.Single(items);
        // RunningToolItem is internal, verify completion through reflection
        var runningTool = items[0];
        Assert.Equal("RunningToolItem", runningTool.GetType().Name);
        var isCompleteProperty = runningTool.GetType().GetProperty("IsComplete");
        Assert.NotNull(isCompleteProperty);
        Assert.True((bool)isCompleteProperty.GetValue(runningTool)!);
    }

    [Fact]
    public void MultipleTools_TrackedIndependently()
    {
        // Arrange
        var feedView = new FeedView();

        // Act - Start multiple tools
        feedView.AddToolExecutionStart("tool1", "Tool 1");
        feedView.AddToolExecutionStart("tool2", "Tool 2");

        // Add details to different tools
        feedView.AddToolExecutionDetail("tool1", "Tool 1 processing...");
        feedView.AddToolExecutionDetail("tool2", "Tool 2 processing...");

        // Complete them in different order
        feedView.AddToolExecutionComplete("tool2", true, "0.8s");
        feedView.AddToolExecutionComplete("tool1", false, "1.2s", "Error occurred");

        // Assert
        var items = feedView.GetItemsForTesting();
        Assert.Equal(2, items.Count);

        // Both should be RunningToolItem instances and complete
        foreach (var item in items)
        {
            Assert.Equal("RunningToolItem", item.GetType().Name);
            var isCompleteProperty = item.GetType().GetProperty("IsComplete");
            Assert.NotNull(isCompleteProperty);
            Assert.True((bool)isCompleteProperty.GetValue(item)!);
        }
    }
}