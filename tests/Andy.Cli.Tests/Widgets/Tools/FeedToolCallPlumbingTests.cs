using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Widgets;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// End-to-end plumbing (#249): a tool's structured result must reach its presenter unflattened.
/// Before this path existed, Data was reduced to a string in the executor and Metadata - which
/// carries most of the counts - never reached the UI at all.
/// </summary>
public class FeedToolCallPlumbingTests
{
    private static ToolCallItem SingleToolCall(FeedView feed)
        => feed.GetItemsForTesting().OfType<ToolCallItem>().Single();

    [Fact]
    public void ToolsWithAPresenterRenderThroughTheNewItem()
    {
        var feed = new FeedView();

        feed.AddToolExecutionStart("execute_command_1", "execute_command",
            new Dictionary<string, object?> { ["command"] = "ls" });

        Assert.Single(feed.GetItemsForTesting().OfType<ToolCallItem>());
    }

    [Fact]
    public void ToolsWithoutAPresenterKeepTheLegacyItem()
    {
        // The migration is incremental: a tool whose presenter has not been written yet must
        // keep rendering exactly as it did.
        var feed = new FeedView();

        // todo_management has no presenter yet (#258), so it must render exactly as before.
        feed.AddToolExecutionStart("todo_management_1", "todo_management",
            new Dictionary<string, object?> { ["action"] = "list" });

        Assert.Empty(feed.GetItemsForTesting().OfType<ToolCallItem>());
        Assert.NotEmpty(feed.GetItemsForTesting().OfType<RunningToolItem>());
    }

    [Fact]
    public void StructuredDataAndMetadataSurviveIntoTheSnapshot()
    {
        var feed = new FeedView();
        feed.AddToolExecutionStart("execute_command_1", "execute_command", new Dictionary<string, object?>());

        var data = new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "hello" };
        var metadata = new Dictionary<string, object?> { ["shell"] = "zsh" };

        var matched = feed.CompleteToolCall("execute_command_1", new ToolCallCompletion
        {
            IsSuccessful = true,
            Data = data,
            Metadata = metadata,
            Duration = TimeSpan.FromMilliseconds(1500)
        });

        Assert.True(matched);
        var snapshot = SingleToolCall(feed).Snapshot;
        Assert.Same(data, snapshot.Data);                       // not stringified on the way in
        Assert.Equal("zsh", snapshot.Metadata["shell"]);        // metadata now reaches the UI
        Assert.Equal(TimeSpan.FromMilliseconds(1500), snapshot.Duration);
        Assert.True(snapshot.Succeeded);
    }

    [Fact]
    public void ArgumentsArrivingAfterTheCallStartedAreAttached()
    {
        var feed = new FeedView();
        feed.AddToolExecutionStart("execute_command_1", "execute_command", new Dictionary<string, object?>());

        feed.UpdateToolByExactId("execute_command_1",
            new Dictionary<string, object?> { ["command"] = "git status" });

        Assert.Equal("git status", SingleToolCall(feed).Snapshot.Parameters["command"]);
    }

    [Fact]
    public void CompletionIsIdempotent()
    {
        // The executor completes a call the moment the tool returns; a later end-of-turn pass
        // must not overwrite that with whole-turn timing.
        var feed = new FeedView();
        feed.AddToolExecutionStart("execute_command_1", "execute_command", new Dictionary<string, object?>());

        feed.CompleteToolCall("execute_command_1", new ToolCallCompletion
        {
            IsSuccessful = true,
            Duration = TimeSpan.FromMilliseconds(300)
        });
        feed.CompleteToolCall("execute_command_1", new ToolCallCompletion
        {
            IsSuccessful = false,
            Duration = TimeSpan.FromMinutes(5)
        });

        var snapshot = SingleToolCall(feed).Snapshot;
        Assert.True(snapshot.IsSuccessful);
        Assert.Equal(TimeSpan.FromMilliseconds(300), snapshot.Duration);
    }

    [Fact]
    public void CompletingAnUnknownCallReportsNoMatch()
    {
        var feed = new FeedView();

        Assert.False(feed.CompleteToolCall("never_started_1",
            new ToolCallCompletion { IsSuccessful = true }));
    }

    [Fact]
    public void LegacyCompletionDoesNotDisturbTheNewItem()
    {
        // AddToolExecutionComplete still runs for every tool; it must leave presenter-backed
        // calls alone rather than double-completing them with a pre-rendered string.
        var feed = new FeedView();
        feed.AddToolExecutionStart("execute_command_1", "execute_command", new Dictionary<string, object?>());
        feed.CompleteToolCall("execute_command_1", new ToolCallCompletion
        {
            IsSuccessful = true,
            Data = new Dictionary<string, object?> { ["stdout"] = "real output" }
        });

        feed.AddToolExecutionComplete("execute_command_1", false, "9.9s", "a pre-rendered summary");

        var snapshot = SingleToolCall(feed).Snapshot;
        Assert.True(snapshot.IsSuccessful);
        Assert.Equal("real output", Andy.Cli.Services.ToolResults.ToolData.GetString(snapshot.Data, "stdout"));
    }

    [Fact]
    public void RenderedRowsShowTheStructuredResult()
    {
        var feed = new FeedView();
        feed.AddToolExecutionStart("execute_command_1", "execute_command",
            new Dictionary<string, object?> { ["command"] = "git status" });
        feed.CompleteToolCall("execute_command_1", new ToolCallCompletion
        {
            IsSuccessful = true,
            Data = new Dictionary<string, object?>
            {
                ["command"] = "git status",
                ["exit_code"] = 0,
                ["stdout"] = "nothing to commit, working tree clean",
                ["duration_ms"] = 240.0
            }
        });

        var rows = SingleToolCall(feed).DebugRows(80);

        Assert.Contains("Ran git status", rows[0]);
        Assert.Contains(rows, r => r.Contains("working tree clean"));
    }
}
