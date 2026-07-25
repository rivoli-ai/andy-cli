using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Themes;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// git_diff presentation (#257). The tool returns its output pre-rendered as markdown prose, so
/// these tests pin the reader that recovers per-file structure from it, and the presentation
/// built on top. When the tool starts returning structured data the reader can be deleted and
/// only its tests go with it.
/// </summary>
public class GitDiffPresenterTests
{
    // The shape GitDiffFormatter produces today, emoji headings included.
    private const string FormattedOutput = """
        📊 **Change Summary**

          `src/Program.cs` (5 changes, +3, -2)

        📄 **src/Program.cs** (5 modifications)
           **+3** additions, **-2** deletions

          Lines 10-14:

        ```diff
        +   10: var updated = true;
        -   11: var old = false;
             12: return updated;
        ```

        📄 **README.md** (1 modifications)
           **+1** additions

          Lines 3-3:

        ```diff
        +    3: a new line
        ```
        """;

    private static ToolCallSnapshot Snapshot(object? data, bool complete = true, bool successful = true) => new()
    {
        ToolId = "git_diff_1",
        ToolName = "git_diff",
        Parameters = new Dictionary<string, object?>(),
        IsComplete = complete,
        IsSuccessful = successful,
        Data = data
    };

    private static ToolPresentation Present(ToolCallSnapshot snapshot, int width = 100, bool expanded = false)
        => new GitDiffToolPresenter().Present(snapshot, new ToolPresentationContext(width, expanded, Theme.Current));

    [Fact]
    public void ReaderRecoversEachChangedFile()
    {
        var files = GitDiffOutputReader.Read(FormattedOutput);

        Assert.Equal(2, files.Count);
        Assert.Equal("src/Program.cs", files[0].Path);
        Assert.Equal(3, files[0].Added);
        Assert.Equal(2, files[0].Removed);
        Assert.Equal("README.md", files[1].Path);
    }

    [Fact]
    public void ReaderClassifiesAddedRemovedAndContextLines()
    {
        var lines = GitDiffOutputReader.Read(FormattedOutput)[0].Diff.Lines;

        Assert.Contains(lines, l => l.Kind == DiffLineKind.Added && l.Text.Contains("var updated"));
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Removed && l.Text.Contains("var old"));
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Context && l.Text.Contains("return updated"));
    }

    [Fact]
    public void ReaderKeepsTheLineNumbersOnTheCorrectSide()
    {
        var lines = GitDiffOutputReader.Read(FormattedOutput)[0].Diff.Lines;

        var added = lines.First(l => l.Kind == DiffLineKind.Added);
        var removed = lines.First(l => l.Kind == DiffLineKind.Removed);

        Assert.Equal(10, added.NewLineNumber);
        Assert.Null(added.OldLineNumber);
        Assert.Equal(11, removed.OldLineNumber);
        Assert.Null(removed.NewLineNumber);
    }

    [Fact]
    public void ReaderReturnsNothingForUnrecognizedOutput()
    {
        // A change to the upstream formatter must degrade to plain text, not corrupt the display.
        Assert.Empty(GitDiffOutputReader.Read("some entirely different output"));
        Assert.Empty(GitDiffOutputReader.Read(""));
        Assert.Empty(GitDiffOutputReader.Read(null));
    }

    [Fact]
    public void HeaderSummarizesTheWholeChange()
    {
        var presentation = Present(Snapshot(FormattedOutput));

        Assert.Equal("Diff", presentation.Header.Text);
        Assert.Equal("2 files changed  +4 -2", presentation.Trailing);
    }

    [Fact]
    public void EachFileGetsItsOwnHeaderWithCounts()
    {
        var rows = Present(Snapshot(FormattedOutput)).Body.Select(r => r.Text).ToList();

        Assert.Contains(rows, r => r.Contains("src/Program.cs") && r.Contains("+3 -2"));
        Assert.Contains(rows, r => r.Contains("README.md") && r.Contains("+1"));
    }

    [Fact]
    public void DiffRowsAreColoredNotPlainText()
    {
        // Previously every line - added, removed, context - was the same dim gray.
        var body = Present(Snapshot(FormattedOutput), expanded: true).Body;

        Assert.Contains(body, r => r.Spans.Any(s => s.Background == Theme.Current.DiffAddedBackground));
        Assert.Contains(body, r => r.Spans.Any(s => s.Background == Theme.Current.DiffRemovedBackground));
    }

    [Fact]
    public void EmojiHeadingsDoNotReachTheFeed()
    {
        var text = string.Join("\n", Present(Snapshot(FormattedOutput), expanded: true).Body.Select(r => r.Text));

        Assert.DoesNotContain("📄", text);
        Assert.DoesNotContain("📊", text);
        Assert.DoesNotContain("```", text);
    }

    [Fact]
    public void NoChangesIsStatedExplicitly()
    {
        Assert.Contains("(no changes)",
            Present(Snapshot("No changes to display")).Body.Select(r => r.Text));
    }

    [Fact]
    public void UnrecognizedOutputStillRendersAsText()
    {
        var presentation = Present(Snapshot("a wholly unexpected payload\nwith two lines"));

        var rows = presentation.Body.Select(r => r.Text).ToList();
        Assert.Contains(rows, r => r.Contains("wholly unexpected"));
        Assert.Contains(rows, r => r.Contains("with two lines"));
    }

    [Fact]
    public void BlankContextLinesSurvive()
    {
        // The old rendering filtered blank lines out of "raw output" tools, which corrupts a diff.
        const string withBlank = """
            📄 **a.txt** (2 modifications)
               **+1** additions

            ```diff
            +    1: first
                 2:
            +    3: third
            ```
            """;

        var lines = GitDiffOutputReader.Read(withBlank)[0].Diff.Lines;

        Assert.Contains(lines, l => l.Kind == DiffLineKind.Context && l.Text.Length == 0);
    }

    [Fact]
    public void FailedDiffShowsTheError()
    {
        var snapshot = Snapshot(null, successful: false) with { ErrorMessage = "not a git repository" };

        Assert.Contains(Present(snapshot).Body.Select(r => r.Text), t => t.Contains("not a git repository"));
    }
}
