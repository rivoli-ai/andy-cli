using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Regression for the "list_directory shows L done / (loading...)" bug: the feed read the wrong
/// result key ("entries" instead of the tool's real "items"), gated the whole summary behind
/// parameters that often never reached the UI, and only ever produced a one-line count. The result
/// must now render the actual directory listing (counts + entry names) straight from the result
/// data - even when the parameters did not arrive.
/// </summary>
public class ListDirectoryResultDisplayTests
{
    // Mimics Andy.Tools' FileSystemEntry (read by reflection: Name + IsDirectory).
    private sealed class Entry
    {
        public string Name { get; init; } = "";
        public bool IsDirectory { get; init; }
    }

    private static RunningToolItem CompletedListDir(Dictionary<string, object?>? parameters)
    {
        var feed = new FeedView();
        feed.AddToolExecutionStart("list_directory_1", "list_directory",
            new Dictionary<string, object?> { ["__toolId"] = "list_directory_1" });

        var resultData = new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Entry { Name = "sub", IsDirectory = true },
                new Entry { Name = "alpha.txt", IsDirectory = false },
                new Entry { Name = "beta.cs", IsDirectory = false },
            },
            ["count"] = 3,
            ["total_count"] = 3,
        };

        // UpdateToolResult must match the still-running item, so run it before completing.
        feed.UpdateToolResult("list_directory_1", "list_directory", success: true, resultData, parameters);

        var item = feed.GetItemsForTesting().OfType<RunningToolItem>().First();
        item.SetComplete(true, "0.1s");
        return item;
    }

    private static string Render(RunningToolItem item, bool expanded)
    {
        ToolOutputView.Expanded = expanded;
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 80, 0, item.MeasureLineCount(80), new DL.DisplayListBuilder().Build(), b);
        return string.Concat(b.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content));
    }

    [Fact]
    public void ShowsListing_EvenWhenParametersNeverArrived()
    {
        var item = CompletedListDir(parameters: null); // the live "loading..." case
        var text = Render(item, expanded: false);

        Assert.Contains("3 items", text);            // real count, not "done"
        Assert.Contains("1 dir", text);
        Assert.Contains("2 files", text);
        Assert.Contains("sub/", text);               // actual entry names
        Assert.Contains("alpha.txt", text);
        Assert.DoesNotContain("done", text);
        Assert.DoesNotContain("loading", text);
    }

    [Fact]
    public void UsesDirectoryNameFromParameters_WhenPresent()
    {
        var item = CompletedListDir(new Dictionary<string, object?> { ["path"] = "/tmp/project/src" });
        var text = Render(item, expanded: false);
        Assert.Contains("Listed src", text);
        Assert.Contains("beta.cs", text);
    }

    [Fact]
    public void CompletedTool_WithoutParams_DoesNotShowLoadingInHeader()
    {
        var item = CompletedListDir(parameters: null);
        var header = Render(item, expanded: false);
        Assert.DoesNotContain("loading...", header);
    }
}
