using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Themes;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Presenters for read_file (#252), search_text (#255) and the filesystem tools (#256). Each
/// fact asserted here is read from the structured payload the tool returned, never from a string
/// an earlier layer rendered.
/// </summary>
public class ReadSearchFileSystemPresenterTests
{
    private sealed class Entry
    {
        public string Name { get; init; } = "";
        public bool IsDirectory { get; init; }
    }

    private sealed class Match
    {
        public string FilePath { get; init; } = "";
        public int? LineNumber { get; init; }
        public string FullLine { get; init; } = "";
        public string MatchText { get; init; } = "";
    }

    private static ToolCallSnapshot Snapshot(string tool, object? data = null,
        Dictionary<string, object?>? metadata = null, Dictionary<string, object?>? parameters = null,
        bool complete = true, bool successful = true) => new()
        {
            ToolId = tool + "_1",
            ToolName = tool,
            Parameters = parameters ?? new Dictionary<string, object?>(),
            IsComplete = complete,
            IsSuccessful = successful,
            Data = data,
            Metadata = metadata ?? new Dictionary<string, object?>()
        };

    private static ToolPresentation Present(IToolPresenter presenter, ToolCallSnapshot snapshot,
        int width = 80, bool expanded = false)
        => presenter.Present(snapshot, new ToolPresentationContext(width, expanded, Theme.Current));

    // ---- read_file (#252) ----------------------------------------------------------------

    [Fact]
    public void ReadShowsPathAndLineCountFromMetadata()
    {
        // The count comes from the tool's line_count field, not from a regex over "N lines read".
        var snapshot = Snapshot("read_file",
            data: new Dictionary<string, object?> { ["content"] = "a\nb", ["line_count"] = 240, ["file_size_formatted"] = "8.4 KB" },
            parameters: new Dictionary<string, object?> { ["file_path"] = "src/Program.cs" });

        var presentation = Present(new ReadFileToolPresenter(), snapshot);

        Assert.Equal("Read src/Program.cs", presentation.Header.Text);
        Assert.Contains("240 lines", presentation.Trailing);
        Assert.Contains("8.4 KB", presentation.Trailing);
    }

    [Fact]
    public void ReadShowsTheRequestedRangeRatherThanJustTruncated()
    {
        var snapshot = Snapshot("read_file",
            data: new Dictionary<string, object?> { ["line_count"] = 51, ["start_line"] = 100, ["end_line"] = 150 },
            parameters: new Dictionary<string, object?> { ["file_path"] = "a.cs" });

        Assert.Equal("Read a.cs:100-150", Present(new ReadFileToolPresenter(), snapshot).Header.Text);
    }

    [Fact]
    public void SuccessfulReadHasNoBody()
    {
        // The content is the model's input; echoing it would bury the conversation.
        var snapshot = Snapshot("read_file",
            data: new Dictionary<string, object?> { ["content"] = new string('x', 5000), ["line_count"] = 90 },
            parameters: new Dictionary<string, object?> { ["file_path"] = "a.cs" });

        Assert.Empty(Present(new ReadFileToolPresenter(), snapshot).Body);
    }

    [Fact]
    public void FailedReadShowsTheError()
    {
        var snapshot = Snapshot("read_file", successful: false,
            parameters: new Dictionary<string, object?> { ["file_path"] = "missing.cs" }) with
        {
            ErrorMessage = "File not found: missing.cs"
        };

        var presentation = Present(new ReadFileToolPresenter(), snapshot);

        Assert.Contains(presentation.Body.Select(r => r.Text), t => t.Contains("File not found"));
        Assert.Equal(Theme.Current.Error, presentation.Body[0].Spans[0].Foreground);
    }

    [Fact]
    public void RunningReadUsesThePresentTense()
    {
        var snapshot = Snapshot("read_file", complete: false,
            parameters: new Dictionary<string, object?> { ["file_path"] = "a.cs" });

        Assert.StartsWith("Reading ", Present(new ReadFileToolPresenter(), snapshot).Header.Text);
    }

    // ---- search_text (#255) --------------------------------------------------------------

    [Fact]
    public void SearchFoldsCountsIntoTheHeaderRow()
    {
        var snapshot = Snapshot("search_text",
            data: new Dictionary<string, object?> { ["items"] = Array.Empty<object>(), ["count"] = 12 },
            metadata: new Dictionary<string, object?>
            {
                ["search_pattern"] = "AddToolExecution",
                ["target_path"] = "src",
                ["total_matches"] = 12,
                ["files_with_matches"] = 4
            });

        var presentation = Present(new SearchTextToolPresenter(), snapshot);

        Assert.Equal("Search \"AddToolExecution\" in src", presentation.Header.Text);
        Assert.Equal("12 matches in 4 files", presentation.Trailing);
    }

    [Fact]
    public void SearchWithNoMatchesSaysSoExplicitly()
    {
        // Zero matches is a finding, not a silent success.
        var snapshot = Snapshot("search_text",
            data: new Dictionary<string, object?> { ["items"] = Array.Empty<object>(), ["count"] = 0 },
            metadata: new Dictionary<string, object?> { ["search_pattern"] = "nope", ["total_matches"] = 0 });

        var presentation = Present(new SearchTextToolPresenter(), snapshot);

        Assert.Contains("(no matches)", presentation.Body.Select(r => r.Text));
        Assert.Equal(Theme.Current.Warning, presentation.Body[0].Spans[0].Foreground);
    }

    [Fact]
    public void SearchShowsMatchesAsPathAndLine()
    {
        var snapshot = Snapshot("search_text",
            data: new Dictionary<string, object?>
            {
                ["items"] = new object[]
                {
                    new Match { FilePath = "src/FeedView.cs", LineNumber = 213, FullLine = "  public void AddToolExecution(...)", MatchText = "AddToolExecution" }
                }
            },
            metadata: new Dictionary<string, object?> { ["search_pattern"] = "AddToolExecution", ["total_matches"] = 1 });

        var presentation = Present(new SearchTextToolPresenter(), snapshot);

        Assert.Contains("src/FeedView.cs:213", presentation.Body[0].Text);
        Assert.Contains("AddToolExecution", presentation.Body[0].Text);
        // The matched substring is picked out inside the line.
        Assert.Contains(presentation.Body[0].Spans, s => s.Text == "AddToolExecution" && s.Foreground == Theme.Current.Warning);
    }

    [Fact]
    public void SearchCapsMatchesInCollapsedModeAndSaysHowMany()
    {
        var matches = Enumerable.Range(1, 20)
            .Select(i => (object)new Match { FilePath = $"f{i}.cs", LineNumber = i, FullLine = "hit", MatchText = "hit" })
            .ToArray();
        var snapshot = Snapshot("search_text",
            data: new Dictionary<string, object?> { ["items"] = matches },
            metadata: new Dictionary<string, object?> { ["search_pattern"] = "hit", ["total_matches"] = 20 });

        var collapsed = Present(new SearchTextToolPresenter(), snapshot);
        var expanded = Present(new SearchTextToolPresenter(), snapshot, expanded: true);

        Assert.Equal(4, collapsed.Body.Count);                     // 3 hits + the omission marker
        Assert.Contains("+17", collapsed.Body[^1].Text);
        Assert.True(expanded.Body.Count > collapsed.Body.Count);
    }

    [Fact]
    public void SearchReportsWhenTheToolCappedItsOwnResults()
    {
        // "12 matches" and "at least 12 matches" mean different things.
        var snapshot = Snapshot("search_text",
            data: new Dictionary<string, object?> { ["items"] = Array.Empty<object>() },
            metadata: new Dictionary<string, object?>
            {
                ["search_pattern"] = "x",
                ["total_matches"] = 500,
                ["results_truncated"] = true
            });

        Assert.Contains("capped", Present(new SearchTextToolPresenter(), snapshot).Trailing);
    }

    // ---- list_directory and mutations (#256) ---------------------------------------------

    [Fact]
    public void ListDirectoryReportsCountsFromMetadata()
    {
        var snapshot = Snapshot("list_directory",
            data: new Dictionary<string, object?> { ["items"] = Array.Empty<object>() },
            metadata: new Dictionary<string, object?>
            {
                ["directory_path"] = "src/Andy.Cli/Widgets",
                ["file_count"] = 28,
                ["directory_count"] = 3
            });

        var presentation = Present(new ListDirectoryToolPresenter(), snapshot);

        Assert.Equal("List src/Andy.Cli/Widgets", presentation.Header.Text);
        Assert.Equal("28 files, 3 directories", presentation.Trailing);
    }

    [Fact]
    public void EmptyDirectoryIsStatedRatherThanShownAsZero()
    {
        var snapshot = Snapshot("list_directory",
            data: new Dictionary<string, object?> { ["items"] = Array.Empty<object>() },
            metadata: new Dictionary<string, object?> { ["file_count"] = 0, ["directory_count"] = 0 });

        Assert.Equal("(empty)", Present(new ListDirectoryToolPresenter(), snapshot).Trailing);
    }

    [Fact]
    public void ListDirectoryShowsNoEntriesUntilExpanded()
    {
        var snapshot = Snapshot("list_directory",
            data: new Dictionary<string, object?>
            {
                ["items"] = new object[] { new Entry { Name = "a.cs" }, new Entry { Name = "b", IsDirectory = true } }
            },
            metadata: new Dictionary<string, object?> { ["file_count"] = 1, ["directory_count"] = 1 });

        Assert.Empty(Present(new ListDirectoryToolPresenter(), snapshot).Body);
        Assert.NotEmpty(Present(new ListDirectoryToolPresenter(), snapshot, expanded: true).Body);
    }

    [Theory]
    [InlineData("create_directory", "Created directory ")]
    [InlineData("delete_file", "Deleted ")]
    public void SinglePathMutationsStateWhatChanged(string tool, string expectedPrefix)
    {
        var snapshot = Snapshot(tool,
            parameters: new Dictionary<string, object?> { ["path"] = "build/output" });

        Assert.StartsWith(expectedPrefix, Present(new FileMutationToolPresenter(), snapshot).Header.Text);
    }

    [Fact]
    public void DeletionsAreVisuallyDistinct()
    {
        // Destructive operations should be easy to spot when scanning back through a session.
        var snapshot = Snapshot("delete_file",
            parameters: new Dictionary<string, object?> { ["file_path"] = "old.cs" });

        var header = Present(new FileMutationToolPresenter(), snapshot).Header;

        Assert.Contains(header.Spans, s => s.Text == "old.cs" && s.Foreground == Theme.Current.Warning);
    }

    [Fact]
    public void CopyAndMoveShowBothPaths()
    {
        var snapshot = Snapshot("move_file", parameters: new Dictionary<string, object?>
        {
            ["source_path"] = "a.cs",
            ["destination_path"] = "b.cs"
        });

        Assert.Equal("Moved a.cs -> b.cs", Present(new FileMutationToolPresenter(), snapshot).Header.Text);
    }

    [Fact]
    public void ExistingDirectoryIsReportedAsANoOp()
    {
        var snapshot = Snapshot("create_directory",
            data: new Dictionary<string, object?> { ["already_exists"] = true },
            parameters: new Dictionary<string, object?> { ["path"] = "src" });

        Assert.Equal("already existed", Present(new FileMutationToolPresenter(), snapshot).Trailing);
    }

    // ---- registry ------------------------------------------------------------------------

    [Theory]
    [InlineData("execute_command", typeof(ShellToolPresenter))]
    [InlineData("read_file", typeof(ReadFileToolPresenter))]
    [InlineData("search_text", typeof(SearchTextToolPresenter))]
    [InlineData("list_directory", typeof(ListDirectoryToolPresenter))]
    [InlineData("delete_file", typeof(FileMutationToolPresenter))]
    public void RegistryResolvesDedicatedPresenters(string tool, Type expected)
    {
        Assert.IsType(expected, ToolPresenterRegistry.Default.Resolve(tool));
    }

    [Fact]
    public void RegistryStripsTheExecutionCounterSuffix()
    {
        Assert.IsType<ReadFileToolPresenter>(ToolPresenterRegistry.Default.Resolve("read_file_7"));
    }

    [Fact]
    public void RegistryFallsBackToTheGenericPresenter()
    {
        Assert.Null(ToolPresenterRegistry.Default.TryResolve("some_unknown_tool"));
        Assert.IsType<GenericToolPresenter>(ToolPresenterRegistry.Default.Resolve("some_unknown_tool"));
    }
}
