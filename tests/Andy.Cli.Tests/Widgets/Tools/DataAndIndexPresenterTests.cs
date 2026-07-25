using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Themes;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Presenters for code_index (#260), the dataframe family (#261) and the PDF family (#262), plus
/// the table renderer they share.
/// </summary>
public class DataAndIndexPresenterTests
{
    private static ToolCallSnapshot Snapshot(string tool, object? data = null,
        Dictionary<string, object?>? parameters = null, bool complete = true, bool successful = true) => new()
        {
            ToolId = tool + "_1",
            ToolName = tool,
            Parameters = parameters ?? new Dictionary<string, object?>(),
            IsComplete = complete,
            IsSuccessful = successful,
            Data = data
        };

    private static ToolPresentation Present(IToolPresenter presenter, ToolCallSnapshot snapshot,
        int width = 90, bool expanded = false)
        => presenter.Present(snapshot, new ToolPresentationContext(width, expanded, Theme.Current));

    // ---- TableRenderer -------------------------------------------------------------------

    [Fact]
    public void TableAlignsColumnsAndRightAlignsNumbers()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "alpha", "1" },
            new[] { "b", "1000" }
        };

        var table = TableRenderer.Render(new[] { "name", "count" }, rows, width: 40, maxRows: 10);

        Assert.Equal(3, table.Count);
        // Every row is the same width, so the columns line up.
        Assert.Equal(table[0].Width, table[1].Width);
        Assert.Equal(table[1].Width, table[2].Width);
        // Numbers right-align, so magnitudes can be compared at a glance.
        Assert.EndsWith("   1", table[1].Text);
        Assert.EndsWith("1000", table[2].Text);
    }

    [Fact]
    public void TableDropsColumnsThatDoNotFitRatherThanSqueezingAllOfThem()
    {
        var rows = new List<IReadOnlyList<string>> { new[] { "aaaaaaaa", "bbbbbbbb", "cccccccc" } };

        var table = TableRenderer.Render(new[] { "one", "two", "three" }, rows, width: 20, maxRows: 10);

        Assert.Contains("more columns", table[0].Text);
    }

    [Fact]
    public void TableReportsTheRowsItLeftOut()
    {
        var rows = Enumerable.Range(1, 50)
            .Select(i => (IReadOnlyList<string>)new[] { i.ToString() })
            .ToList();

        var table = TableRenderer.Render(new[] { "n" }, rows, width: 20, maxRows: 6);

        Assert.Equal(6, table.Count);
        Assert.Contains("+", table[^1].Text);
    }

    [Fact]
    public void NullCellsAreVisibleRatherThanBlank()
    {
        Assert.Equal("-", TableRenderer.Cell(null));
        Assert.Equal("true", TableRenderer.Cell(true));
        Assert.Equal("1.25", TableRenderer.Cell(1.25));
    }

    // ---- code_index (#260) ---------------------------------------------------------------

    [Fact]
    public void StructureQueryReportsCountsFromTypedFields()
    {
        // These used to be recovered with regexes over a string an earlier layer had rendered.
        var snapshot = Snapshot("code_index", new Dictionary<string, object?>
        {
            ["query_type"] = "structure",
            ["data"] = new Dictionary<string, object?>
            {
                ["file_count"] = 412,
                ["namespace_count"] = 38,
                ["class_count"] = 517
            }
        });

        var presentation = Present(new CodeIndexToolPresenter(), snapshot);

        Assert.Equal("Project structure", presentation.Header.Text);
        Assert.Equal("412 files, 38 namespaces, 517 classes", presentation.Trailing);
    }

    [Fact]
    public void SymbolQueryReportsSymbolAndFileCounts()
    {
        var snapshot = Snapshot("code_index", new Dictionary<string, object?>
        {
            ["query_type"] = "symbols",
            ["data"] = new Dictionary<string, object?>
            {
                ["symbols"] = new object[]
                {
                    new Dictionary<string, object?> { ["name"] = "FeedView", ["filePath"] = "src/FeedView.cs", ["line"] = 47, ["kind"] = "class" },
                    new Dictionary<string, object?> { ["name"] = "AddItem", ["filePath"] = "src/FeedView.cs", ["line"] = 108, ["kind"] = "method" },
                    new Dictionary<string, object?> { ["name"] = "Theme", ["filePath"] = "src/Theme.cs", ["line"] = 12, ["kind"] = "class" }
                }
            }
        }, new Dictionary<string, object?> { ["pattern"] = "Feed" });

        var presentation = Present(new CodeIndexToolPresenter(), snapshot);

        Assert.Contains("Search code", presentation.Header.Text);
        Assert.Contains("\"Feed\"", presentation.Header.Text);
        Assert.Equal("3 symbols in 2 files", presentation.Trailing);
    }

    [Fact]
    public void ExpandedSymbolsShowFileAndLine()
    {
        var snapshot = Snapshot("code_index", new Dictionary<string, object?>
        {
            ["query_type"] = "symbols",
            ["data"] = new Dictionary<string, object?>
            {
                ["symbols"] = new object[]
                {
                    new Dictionary<string, object?> { ["name"] = "FeedView", ["filePath"] = "src/FeedView.cs", ["line"] = 47, ["kind"] = "class" }
                }
            }
        });

        var body = Present(new CodeIndexToolPresenter(), snapshot, expanded: true).Body;

        Assert.Contains("src/FeedView.cs:47", body[0].Text);
        Assert.Contains("class FeedView", body[0].Text);
    }

    [Fact]
    public void EmptyIndexResultIsStated()
    {
        var snapshot = Snapshot("code_index", new Dictionary<string, object?>
        {
            ["query_type"] = "symbols",
            ["data"] = new Dictionary<string, object?> { ["count"] = 0 }
        });

        Assert.Equal("(no matches)", Present(new CodeIndexToolPresenter(), snapshot).Trailing);
    }

    // ---- dataframe (#261) ----------------------------------------------------------------

    private static Dictionary<string, object?> Envelope(int rowCount, bool truncated = false) => new()
    {
        ["success"] = true,
        ["dataset_id"] = "sales",
        ["schema"] = new object[]
        {
            new Dictionary<string, object?> { ["name"] = "region", ["type"] = "VARCHAR", ["nullable"] = true },
            new Dictionary<string, object?> { ["name"] = "revenue", ["type"] = "DECIMAL(12,2)", ["nullable"] = false }
        },
        ["row_count"] = rowCount,
        ["preview_rows"] = new object[]
        {
            new Dictionary<string, object?> { ["region"] = "EMEA", ["revenue"] = 1200.5 },
            new Dictionary<string, object?> { ["region"] = "APAC", ["revenue"] = 980.0 }
        },
        ["preview_truncated"] = truncated,
        ["warnings"] = Array.Empty<object>()
    };

    [Fact]
    public void DataFramePreviewRendersARealTable()
    {
        var snapshot = Snapshot("dataframe_preview", Envelope(1204),
            new Dictionary<string, object?> { ["dataset_id"] = "sales" });

        var presentation = Present(new DataFrameToolPresenter(), snapshot);

        Assert.Equal("1,204 rows, 2 columns", presentation.Trailing);
        Assert.Contains("region", presentation.Body[0].Text);
        Assert.Contains("revenue", presentation.Body[0].Text);
        Assert.Contains("EMEA", presentation.Body[1].Text);
    }

    [Fact]
    public void ColumnOrderFollowsTheSchemaNotTheFirstRowsKeys()
    {
        var snapshot = Snapshot("dataframe_preview", Envelope(2));

        var headerRow = Present(new DataFrameToolPresenter(), snapshot).Body[0].Text;

        Assert.True(headerRow.IndexOf("region", StringComparison.Ordinal)
                    < headerRow.IndexOf("revenue", StringComparison.Ordinal));
    }

    [Fact]
    public void TruncatedPreviewSaysSo()
    {
        var snapshot = Snapshot("dataframe_preview", Envelope(100000, truncated: true));

        Assert.Contains("(preview truncated)",
            Present(new DataFrameToolPresenter(), snapshot).Body.Select(r => r.Text));
    }

    [Fact]
    public void SchemaOperationRendersColumnsAndTypes()
    {
        var snapshot = Snapshot("dataframe_schema", Envelope(1204));

        var body = Present(new DataFrameToolPresenter(), snapshot).Body.Select(r => r.Text).ToList();

        Assert.Contains(body, t => t.Contains("column") && t.Contains("type"));
        Assert.Contains(body, t => t.Contains("VARCHAR"));
        Assert.Contains(body, t => t.Contains("DECIMAL(12,2)") && t.Contains("not null"));
    }

    [Fact]
    public void DataFrameTablesAreNotIndentedUnderTheGutter()
    {
        var snapshot = Snapshot("dataframe_preview", Envelope(10));

        Assert.False(Present(new DataFrameToolPresenter(), snapshot).IndentBody);
    }

    [Fact]
    public void DataFrameFailureShowsTheToolsMessage()
    {
        var snapshot = Snapshot("dataframe_filter", successful: false) with
        {
            ErrorMessage = "Unknown column 'regionn'"
        };

        Assert.Contains(Present(new DataFrameToolPresenter(), snapshot).Body.Select(r => r.Text),
            t => t.Contains("Unknown column"));
    }

    // ---- pdf (#262) ----------------------------------------------------------------------

    [Fact]
    public void PdfExtractionReportsHowMuchItProduced()
    {
        var snapshot = Snapshot("pdf_extract_text", new Dictionary<string, object?>
        {
            ["page_count"] = 312,
            ["word_count"] = 48120,
            ["text"] = "Annual Report ..."
        }, new Dictionary<string, object?> { ["path"] = "10-K.pdf" });

        var trailing = Present(new PdfToolPresenter(), snapshot).Trailing;

        Assert.Contains("312 pages", trailing);
        Assert.Contains("48,120 words", trailing);
    }

    [Fact]
    public void RequestedPageRangeIsShown()
    {
        // Which part of the document the model actually saw is worth knowing.
        var snapshot = Snapshot("pdf_extract_text",
            new Dictionary<string, object?> { ["text"] = "x" },
            new Dictionary<string, object?> { ["path"] = "a.pdf", ["start_page"] = 44, ["end_page"] = 51 });

        Assert.Contains("pages 44-51", Present(new PdfToolPresenter(), snapshot).Trailing);
    }

    [Fact]
    public void PdfSearchReportsMatches()
    {
        var snapshot = Snapshot("pdf_search",
            new Dictionary<string, object?> { ["total_matches"] = 23 },
            new Dictionary<string, object?> { ["path"] = "10-K.pdf", ["query"] = "revenue" });

        Assert.Contains("23 matches", Present(new PdfToolPresenter(), snapshot).Trailing);
    }

    [Fact]
    public void OutlineRendersAsAnIndentedTree()
    {
        var snapshot = Snapshot("pdf_outline", new Dictionary<string, object?>
        {
            ["outline"] = new object[]
            {
                new Dictionary<string, object?> { ["title"] = "Part I", ["level"] = 0, ["page"] = 1 },
                new Dictionary<string, object?> { ["title"] = "Item 1A. Risk Factors", ["level"] = 1, ["page"] = 12 }
            }
        }, new Dictionary<string, object?> { ["path"] = "10-K.pdf" });

        var body = Present(new PdfToolPresenter(), snapshot, expanded: true).Body;

        Assert.StartsWith("Part I", body[0].Text);
        Assert.StartsWith("  Item 1A", body[1].Text);
        Assert.Contains("p.12", body[1].Text);
    }

    // ---- registry ------------------------------------------------------------------------

    [Theory]
    [InlineData("code_index", typeof(CodeIndexToolPresenter))]
    [InlineData("dataframe_preview", typeof(DataFrameToolPresenter))]
    [InlineData("dataframe_group_by", typeof(DataFrameToolPresenter))]
    [InlineData("pdf_extract_tables", typeof(PdfToolPresenter))]
    [InlineData("todo_management", typeof(TodoToolPresenter))]
    [InlineData("http_request", typeof(WebToolPresenter))]
    [InlineData("system_info", typeof(UtilityToolPresenter))]
    [InlineData("skill", typeof(SkillToolPresenter))]
    [InlineData("git_diff", typeof(GitDiffToolPresenter))]
    [InlineData("write_file", typeof(WriteFileToolPresenter))]
    [InlineData("replace_text", typeof(ReplaceTextToolPresenter))]
    public void RegistryResolvesEveryPresenter(string tool, Type expected)
    {
        Assert.IsType(expected, ToolPresenterRegistry.Default.Resolve(tool));
    }
}
