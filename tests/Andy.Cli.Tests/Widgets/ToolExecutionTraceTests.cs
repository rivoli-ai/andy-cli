using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class ToolExecutionTraceTests
{
    // AddToolExecutionStart appends exactly one RunningToolItem followed by exactly one trailing
    // SpacerItem (visual separation). The assertions below pin the EXACT feed-item sequence rather
    // than filtering, so any stray or duplicate item beyond the expected spacer fails the test and a
    // feed-bloat regression cannot slip through.
    private static IReadOnlyList<IFeedItem> Items(FeedView feed) => feed.GetItemsForTesting();

    private static bool IsRunningTool(IFeedItem item) => item.GetType().Name == "RunningToolItem";

    private static List<IFeedItem> RunningTools(FeedView feed)
        => feed.GetItemsForTesting().Where(IsRunningTool).ToList();

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

        // Assert - exactly one RunningToolItem plus exactly one trailing SpacerItem, nothing else.
        Assert.Collection(Items(feedView),
            item => Assert.True(IsRunningTool(item)),
            item => Assert.IsType<SpacerItem>(item));
    }

    [Fact]
    public void AddToolExecutionDetail_AddsDetailToRunningTool()
    {
        // Arrange
        var feedView = new FeedView();
        feedView.AddToolExecutionStart("bash_command", "Bash Command");

        // Act - details update the existing RunningToolItem in place; they must not append items.
        feedView.AddToolExecutionDetail("bash_command", "Executing: ls -la");
        feedView.AddToolExecutionDetail("bash_command", "Found 10 files");

        // Assert - still exactly one RunningToolItem plus exactly one trailing SpacerItem.
        Assert.Collection(Items(feedView),
            item => Assert.True(IsRunningTool(item)),
            item => Assert.IsType<SpacerItem>(item));
    }

    [Fact]
    public void AddToolExecutionComplete_WithResult_UpdatesToolStatus()
    {
        // Arrange
        var feedView = new FeedView();
        feedView.AddToolExecutionStart("update_file", "Update File");

        // Act - completion updates the existing RunningToolItem in place; it must not append items.
        feedView.AddToolExecutionComplete("update_file", true, "1.5s", "Updated 5 lines");

        // Assert - exactly one RunningToolItem plus exactly one trailing SpacerItem.
        Assert.Collection(Items(feedView),
            item => Assert.True(IsRunningTool(item)),
            item => Assert.IsType<SpacerItem>(item));

        var runningTool = RunningTools(feedView)[0];
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

        // Assert - exactly two tools, each followed by exactly one SpacerItem, in start order.
        Assert.Collection(Items(feedView),
            item => Assert.True(IsRunningTool(item)),
            item => Assert.IsType<SpacerItem>(item),
            item => Assert.True(IsRunningTool(item)),
            item => Assert.IsType<SpacerItem>(item));

        // Both should be complete
        foreach (var item in RunningTools(feedView))
        {
            var isCompleteProperty = item.GetType().GetProperty("IsComplete");
            Assert.NotNull(isCompleteProperty);
            Assert.True((bool)isCompleteProperty.GetValue(item)!);
        }
    }
}
