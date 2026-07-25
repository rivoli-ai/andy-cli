using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Widgets;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Regression for the "list_directory shows L done / (loading...)" bug: the feed read the wrong
/// result key ("entries" instead of the tool's real "items") and gated the whole summary behind
/// parameters that often never reached the UI, so a completed listing could render with no
/// information at all.
///
/// The listing now goes through <see cref="ListDirectoryToolPresenter"/> (#256), which reads the
/// counts from the metadata the tool returns. The requirement this file guards is unchanged: a
/// completed listing must state what was found, with or without parameters. What did change is
/// WHERE the entry names appear - the listing is the model's input, so collapsed mode reports the
/// shape of the directory and expanded mode (ctrl+o) lists it.
/// </summary>
public class ListDirectoryResultDisplayTests : IDisposable
{
    // Mimics Andy.Tools' FileSystemEntry (read by property: Name + IsDirectory).
    private sealed class Entry
    {
        public string Name { get; init; } = "";
        public bool IsDirectory { get; init; }
    }

    public void Dispose() => ToolOutputView.Expanded = false;

    private static ToolCallItem CompletedListDir(Dictionary<string, object?>? parameters)
    {
        var feed = new FeedView();
        feed.AddToolExecutionStart("list_directory_1", "list_directory",
            parameters ?? new Dictionary<string, object?>());

        var data = new Dictionary<string, object?>
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

        // The counts the tool puts on Metadata now reach the UI; they used to be dropped.
        var metadata = new Dictionary<string, object?>
        {
            ["directory_path"] = "/tmp/project/src",
            ["total_entries"] = 3,
            ["file_count"] = 2,
            ["directory_count"] = 1,
        };

        feed.CompleteToolCall("list_directory_1", new ToolCallCompletion
        {
            IsSuccessful = true,
            Data = data,
            Metadata = metadata,
            Duration = TimeSpan.FromMilliseconds(100)
        });

        return feed.GetItemsForTesting().OfType<ToolCallItem>().Single();
    }

    private static string Render(ToolCallItem item, bool expanded)
    {
        ToolOutputView.Expanded = expanded;
        return string.Join("\n", item.DebugRows(80));
    }

    [Fact]
    public void ShowsCounts_EvenWhenParametersNeverArrived()
    {
        var text = Render(CompletedListDir(parameters: null), expanded: false);

        Assert.Contains("2 files", text);
        Assert.Contains("1 directory", text);
        Assert.DoesNotContain("done", text);
        Assert.DoesNotContain("loading", text);
    }

    [Fact]
    public void UsesTheDirectoryPathTheToolReported()
    {
        // The path comes from the result metadata, so it is right even when the parameters were lost.
        var text = Render(CompletedListDir(parameters: null), expanded: false);

        Assert.Contains("List ", text);
        Assert.Contains("src", text);
    }

    [Fact]
    public void ExpandedModeListsTheEntriesWithDirectoriesFirst()
    {
        var text = Render(CompletedListDir(parameters: null), expanded: true);

        Assert.Contains("sub/", text);
        Assert.Contains("alpha.txt", text);
        Assert.Contains("beta.cs", text);
        Assert.True(text.IndexOf("sub/", StringComparison.Ordinal) < text.IndexOf("alpha.txt", StringComparison.Ordinal),
            "directories sort before files");
    }

    [Fact]
    public void CompletedToolDoesNotShowLoadingInHeader()
    {
        Assert.DoesNotContain("loading", Render(CompletedListDir(parameters: null), expanded: false));
    }

    [Fact]
    public void DirectoryClassificationComesFromTheToolNotTheFilename()
    {
        // The old heuristic ("no dot in the name" => directory) called Makefile a directory and
        // a "v1.2" directory a file.
        var feed = new FeedView();
        feed.AddToolExecutionStart("list_directory_1", "list_directory", new Dictionary<string, object?>());
        feed.CompleteToolCall("list_directory_1", new ToolCallCompletion
        {
            IsSuccessful = true,
            Data = new Dictionary<string, object?>
            {
                ["items"] = new object[]
                {
                    new Entry { Name = "Makefile", IsDirectory = false },
                    new Entry { Name = "v1.2", IsDirectory = true },
                }
            },
            Metadata = new Dictionary<string, object?> { ["file_count"] = 1, ["directory_count"] = 1 }
        });

        var item = feed.GetItemsForTesting().OfType<ToolCallItem>().Single();
        var text = Render(item, expanded: true);

        Assert.Contains("v1.2/", text);        // a directory, despite the dot
        Assert.Contains("Makefile", text);
        Assert.DoesNotContain("Makefile/", text); // a file, despite having no extension
    }
}
