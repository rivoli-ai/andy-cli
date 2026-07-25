using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Themes;
using Andy.Cli.Widgets;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Diff rendering and the file-change presenters (#253, #254). The diff is computed by the
/// executor around the call and travels as structured data, so these tests exercise the same
/// FileMutationView the real path produces.
/// </summary>
public class FileChangePresenterTests
{
    private static FileMutationView Mutation(string path, string? before, string after)
    {
        var kind = before is null ? FileChangeKind.Create : FileChangeKind.Update;
        return new FileMutationView(path, kind, UnifiedDiff.Compute(before, after),
            kind == FileChangeKind.Create ? after : null);
    }

    private static ToolCallSnapshot Snapshot(string tool, FileMutationView? mutation,
        Dictionary<string, object?>? parameters = null, object? data = null,
        bool complete = true, bool successful = true) => new()
        {
            ToolId = tool + "_1",
            ToolName = tool,
            Parameters = parameters ?? new Dictionary<string, object?>(),
            IsComplete = complete,
            IsSuccessful = successful,
            Data = data,
            FileMutation = mutation
        };

    private static ToolPresentation Present(IToolPresenter presenter, ToolCallSnapshot snapshot,
        int width = 100, bool expanded = false)
        => presenter.Present(snapshot, new ToolPresentationContext(width, expanded, Theme.Current));

    // ---- DiffRenderer --------------------------------------------------------------------

    [Fact]
    public void DiffRowsCarryLineNumbersSignsAndTints()
    {
        var diff = UnifiedDiff.Compute("alpha\nbravo\ncharlie\n", "alpha\nBRAVO\ncharlie\n");

        var rows = DiffRenderer.RenderDiff(diff, "sample.txt", width: 60, maxRows: 40);

        var removed = rows.Single(r => r.Text.Contains("bravo"));
        var added = rows.Single(r => r.Text.Contains("BRAVO"));

        Assert.Contains(removed.Spans, s => s.Text == "- " && s.Foreground == Theme.Current.Error);
        Assert.Contains(added.Spans, s => s.Text == "+ " && s.Foreground == Theme.Current.Success);
        Assert.Contains(removed.Spans, s => s.Background == Theme.Current.DiffRemovedBackground);
        Assert.Contains(added.Spans, s => s.Background == Theme.Current.DiffAddedBackground);
    }

    [Fact]
    public void DiffContentIsSyntaxHighlightedByExtension()
    {
        // CodeHighlighter was previously wired only to markdown fenced code blocks, never to diffs.
        var diff = UnifiedDiff.Compute("var x = 1;\n", "public var x = 2;\n");

        var rows = DiffRenderer.RenderDiff(diff, "Program.cs", width: 60, maxRows: 40);

        var added = rows.Single(r => r.Text.Contains("public"));
        Assert.Contains(added.Spans, s => s.Text == "public" && s.Foreground == Theme.Current.SyntaxKeyword);
    }

    [Fact]
    public void UnknownFileTypesAreNotTokenizedAsCode()
    {
        Assert.Null(DiffRenderer.LanguageFor("notes.md"));
        Assert.Null(DiffRenderer.LanguageFor("data.csv"));
        Assert.Equal("csharp", DiffRenderer.LanguageFor("a/b/Program.cs"));
        Assert.Equal("python", DiffRenderer.LanguageFor("script.sh"));
    }

    [Fact]
    public void LongDiffsKeepTheHeadAndTheTail()
    {
        var before = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i}"));
        var after = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"CHANGED {i}"));
        var diff = UnifiedDiff.Compute(before, after);

        var rows = DiffRenderer.RenderDiff(diff, "f.txt", width: 60, maxRows: 10);

        Assert.Equal(10, rows.Count);
        Assert.Contains(rows, r => r.Text.Contains("+"));
        Assert.Contains(rows, r => r.Text.Contains("more lines") || r.Text.Contains("... +"));
    }

    [Fact]
    public void ContentRenderingNumbersEveryLine()
    {
        var rows = DiffRenderer.RenderContent("first\nsecond\nthird\n", "f.txt", width: 40, maxRows: 20);

        Assert.Equal(3, rows.Count);
        Assert.StartsWith("  1 ", rows[0].Text);
        Assert.StartsWith("  3 ", rows[2].Text);
        // A trailing newline is punctuation, not a fourth line.
        Assert.DoesNotContain(rows, r => r.Text.Trim() == "4");
    }

    [Theory]
    [InlineData(18, 3, "+18 -3")]
    [InlineData(18, 0, "+18")]
    [InlineData(0, 4, "-4")]
    [InlineData(0, 0, "no change")]
    public void ChangeCountsFormatCompactly(int added, int removed, string expected)
    {
        Assert.Equal(expected, DiffRenderer.FormatChangeCounts(added, removed));
    }

    // ---- write_file (#253) ---------------------------------------------------------------

    [Fact]
    public void CreatingAFileReadsAsACreationAndShowsNumberedContent()
    {
        var snapshot = Snapshot("write_file", Mutation("src/New.cs", before: null, after: "one\ntwo\n"),
            new Dictionary<string, object?> { ["file_path"] = "src/New.cs" });

        var presentation = Present(new WriteFileToolPresenter(), snapshot);

        Assert.Equal("Created src/New.cs", presentation.Header.Text);
        Assert.Equal("2 lines", presentation.Trailing);
        Assert.StartsWith("  1 ", presentation.Body[0].Text);
        // Not a diff: nothing carries a removed tint.
        Assert.DoesNotContain(presentation.Body, r => r.Spans.Any(s => s.Background == Theme.Current.DiffRemovedBackground));
    }

    [Fact]
    public void OverwritingAFileShowsADiffWithCounts()
    {
        var snapshot = Snapshot("write_file", Mutation("a.txt", "x\ny\n", "x\nZ\n"),
            new Dictionary<string, object?> { ["file_path"] = "a.txt" });

        var presentation = Present(new WriteFileToolPresenter(), snapshot);

        Assert.Equal("Wrote a.txt", presentation.Header.Text);
        Assert.Equal("+1 -1", presentation.Trailing);
        Assert.Contains(presentation.Body, r => r.Spans.Any(s => s.Background == Theme.Current.DiffRemovedBackground));
    }

    [Fact]
    public void DiffBodyIsNotIndentedUnderTheGutter()
    {
        // The diff draws its own line-number gutter; an outer gutter would misalign it.
        var snapshot = Snapshot("write_file", Mutation("a.txt", "x\n", "y\n"),
            new Dictionary<string, object?> { ["file_path"] = "a.txt" });

        Assert.False(Present(new WriteFileToolPresenter(), snapshot).IndentBody);
    }

    // ---- replace_text (#254) -------------------------------------------------------------

    [Fact]
    public void EditShowsTheDiffItPreviouslyNeverRendered()
    {
        // replace_text did not match the "Update"/"Edit"/"Write" name test in the old summary
        // branch, so an edit rendered as a header plus one line of raw result.
        var snapshot = Snapshot("replace_text", Mutation("src/Foo.cs", "int a = 1;\n", "int a = 2;\n"),
            new Dictionary<string, object?> { ["target_path"] = "src/Foo.cs" },
            data: new Dictionary<string, object?> { ["total_replacements"] = 1 });

        var presentation = Present(new ReplaceTextToolPresenter(), snapshot);

        Assert.Equal("Edited src/Foo.cs", presentation.Header.Text);
        Assert.Contains("1 replacement", presentation.Trailing);
        Assert.Contains("+1 -1", presentation.Trailing);
        Assert.NotEmpty(presentation.Body);
    }

    [Fact]
    public void EditThatMatchedNothingSaysSo()
    {
        // A zero-match edit is a distinct outcome and must not render as a bare success.
        var snapshot = Snapshot("replace_text", mutation: null,
            parameters: new Dictionary<string, object?> { ["target_path"] = "src/Foo.cs" },
            data: new Dictionary<string, object?> { ["total_replacements"] = 0 });

        Assert.Contains("no matches", Present(new ReplaceTextToolPresenter(), snapshot).Trailing);
    }

    [Fact]
    public void EditAcrossSeveralFilesReportsTheFileCount()
    {
        var snapshot = Snapshot("replace_text", mutation: null,
            parameters: new Dictionary<string, object?> { ["target_path"] = "src" },
            data: new Dictionary<string, object?> { ["total_replacements"] = 12, ["files_modified"] = 4 });

        var trailing = Present(new ReplaceTextToolPresenter(), snapshot).Trailing;

        Assert.Contains("12 replacements", trailing);
        Assert.Contains("4 files", trailing);
    }

    [Fact]
    public void FailedWriteShowsTheError()
    {
        var snapshot = Snapshot("write_file", mutation: null, successful: false,
            parameters: new Dictionary<string, object?> { ["file_path"] = "/readonly/x" }) with
        {
            ErrorMessage = "Access denied"
        };

        var presentation = Present(new WriteFileToolPresenter(), snapshot);

        Assert.Contains(presentation.Body.Select(r => r.Text), t => t.Contains("Access denied"));
    }

    [Fact]
    public void RunningWriteUsesThePresentTense()
    {
        var snapshot = Snapshot("write_file", mutation: null, complete: false,
            parameters: new Dictionary<string, object?> { ["file_path"] = "a.cs" });

        Assert.Equal("Writing a.cs", Present(new WriteFileToolPresenter(), snapshot).Header.Text);
    }

    [Fact]
    public void MeasureAndRenderAgreeForADiffBody()
    {
        var snapshot = Snapshot("write_file", Mutation("a.cs", "one\ntwo\nthree\n", "one\nTWO\nthree\nfour\n"),
            new Dictionary<string, object?> { ["file_path"] = "a.cs" });
        var item = new ToolCallItem(snapshot, new WriteFileToolPresenter());

        foreach (var width in new[] { 30, 60, 100 })
        {
            var b = new Andy.Tui.DisplayList.DisplayListBuilder();
            int measured = item.MeasureLineCount(width);
            item.RenderSlice(0, 0, width, 0, measured, new Andy.Tui.DisplayList.DisplayListBuilder().Build(), b);

            var rows = b.Build().Ops.OfType<Andy.Tui.DisplayList.TextRun>()
                .Where(r => !string.IsNullOrEmpty(r.Content)).Select(r => r.Y).Distinct().ToList();

            Assert.All(rows, y => Assert.InRange(y, 0, measured - 1));
        }
    }
}
