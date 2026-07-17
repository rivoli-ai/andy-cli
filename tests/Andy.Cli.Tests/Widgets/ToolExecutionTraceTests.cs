using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class ToolExecutionTraceTests
{
    // AddToolExecutionStart appends a trailing SpacerItem after each tool (visual separation), so the
    // feed holds more than one item per tool. These tests assert on the RunningToolItem entries only.
    private static List<IFeedItem> RunningTools(FeedView feed)
        => feed.GetItemsForTesting().Where(i => i.GetType().Name == "RunningToolItem").ToList();

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
        var runningTools = RunningTools(feedView);
        Assert.Single(runningTools);
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
        var runningTools = RunningTools(feedView);
        Assert.Single(runningTools);
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
        var runningTools = RunningTools(feedView);
        Assert.Single(runningTools);
        var runningTool = runningTools[0];
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
        var runningTools = RunningTools(feedView);
        Assert.Equal(2, runningTools.Count);

        // Both should be complete
        foreach (var item in runningTools)
        {
            var isCompleteProperty = item.GetType().GetProperty("IsComplete");
            Assert.NotNull(isCompleteProperty);
            Assert.True((bool)isCompleteProperty.GetValue(item)!);
        }
    }
}
