using System;
using System.Collections.Generic;
using System.IO;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Coverage for the human-readable tool call summaries (#223): every tool registered in
/// ToolCatalog maps to a concise present-progressive description, unknown tools fall back
/// to a cleaned-up name plus the most salient argument (never a raw JSON dump), long
/// commands are truncated, and paths are shortened relative to the working directory.
/// </summary>
public class ToolCallSummarizerTests
{
    private static Dictionary<string, object?> P(params (string key, object? value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs) d[key] = value;
        return d;
    }

    // ---- File system tools ----

    [Fact]
    public void ReadFile_ShowsReadingWithPath()
        => Assert.Equal("Reading src/Program.cs",
            ToolCallSummarizer.Summarize("read_file", P(("file_path", "src/Program.cs"))));

    [Fact]
    public void WriteFile_ShowsWritingWithPath()
        => Assert.Equal("Writing notes.txt",
            ToolCallSummarizer.Summarize("write_file", P(("file_path", "notes.txt"), ("content", "hello"))));

    [Fact]
    public void DeleteFile_ShowsDeletingWithPath()
        => Assert.Equal("Deleting old.log",
            ToolCallSummarizer.Summarize("delete_file", P(("file_path", "old.log"))));

    [Fact]
    public void CopyFile_ShowsSourceAndDestination()
        => Assert.Equal("Copying a.txt to b.txt",
            ToolCallSummarizer.Summarize("copy_file", P(("source_path", "a.txt"), ("destination_path", "b.txt"))));

    [Fact]
    public void MoveFile_ShowsSourceAndDestination()
        => Assert.Equal("Moving a.txt to b.txt",
            ToolCallSummarizer.Summarize("move_file", P(("source_path", "a.txt"), ("destination_path", "b.txt"))));

    [Fact]
    public void ListDirectory_ShowsListingWithPath()
        => Assert.Equal("Listing src/Andy.Cli",
            ToolCallSummarizer.Summarize("list_directory", P(("path", "src/Andy.Cli"))));

    [Fact]
    public void ListDirectory_WithoutPath_FallsBackToCurrentDirectory()
        => Assert.Equal("Listing current directory",
            ToolCallSummarizer.Summarize("list_directory", P()));

    [Fact]
    public void CreateDirectory_ShowsCreatingDirectory()
        => Assert.Equal("Creating directory build/out",
            ToolCallSummarizer.Summarize("create_directory", P(("path", "build/out"))));

    // ---- Git / system tools ----

    [Fact]
    public void GitDiff_WithoutPath_ShowsGenericSummary()
        => Assert.Equal("Getting git diff", ToolCallSummarizer.Summarize("git_diff", P()));

    [Fact]
    public void ExecuteCommand_ShowsRunningPrefixWithCommand()
        => Assert.Equal("Running: dotnet build",
            ToolCallSummarizer.Summarize("execute_command", P(("command", "dotnet build"))));

    [Fact]
    public void ExecuteCommand_TruncatesLongCommands()
    {
        var longCommand = new string('x', 200);
        var summary = ToolCallSummarizer.Summarize("execute_command", P(("command", longCommand)));
        Assert.StartsWith("Running: ", summary);
        Assert.EndsWith("...", summary);
        Assert.True(summary.Length <= "Running: ".Length + ToolCallSummarizer.MaxCommandLength,
            $"command summary too long: {summary.Length} chars");
    }

    [Fact]
    public void ExecuteCommand_CollapsesNewlinesInCommand()
    {
        var summary = ToolCallSummarizer.Summarize("execute_command", P(("command", "echo a\necho b")));
        Assert.DoesNotContain("\n", summary);
    }

    [Fact]
    public void ProcessInfo_ShowsInspectingProcesses()
        => Assert.Equal("Inspecting running processes", ToolCallSummarizer.Summarize("process_info", P()));

    [Fact]
    public void SystemInfo_ShowsGettingSystemInformation()
        => Assert.Equal("Getting system information", ToolCallSummarizer.Summarize("system_info", P()));

    // ---- Text tools ----

    [Fact]
    public void FormatText_ShowsOperation()
        => Assert.Equal("Formatting text (upper_case)",
            ToolCallSummarizer.Summarize("format_text", P(("operation", "upper_case"))));

    [Fact]
    public void ReplaceText_ShowsPatternAndTarget()
        => Assert.Equal("Replacing \"foo\" in Program.cs",
            ToolCallSummarizer.Summarize("replace_text", P(("search_pattern", "foo"), ("target_path", "Program.cs"))));

    [Fact]
    public void SearchText_ShowsQuotedPattern()
        => Assert.Equal("Searching for \"TODO\" in src",
            ToolCallSummarizer.Summarize("search_text", P(("search_pattern", "TODO"), ("search_path", "src"))));

    // ---- Utility tools ----

    [Fact]
    public void DatetimeTool_ShowsOperation()
        => Assert.Equal("Getting date/time (now)",
            ToolCallSummarizer.Summarize("datetime_tool", P(("operation", "now"))));

    [Fact]
    public void EncodingTool_EncodeOperation()
        => Assert.Equal("Encoding text (base64_encode)",
            ToolCallSummarizer.Summarize("encoding_tool", P(("operation", "base64_encode"))));

    [Fact]
    public void EncodingTool_DecodeOperation()
        => Assert.Equal("Decoding text (url_decode)",
            ToolCallSummarizer.Summarize("encoding_tool", P(("operation", "url_decode"))));

    [Fact]
    public void EncodingTool_HashOperation()
        => Assert.Equal("Computing hash (sha256_hash)",
            ToolCallSummarizer.Summarize("encoding_tool", P(("operation", "sha256_hash"))));

    // ---- Web tools ----

    [Fact]
    public void HttpRequest_GetShowsFetching()
        => Assert.Equal("Fetching https://example.com/api",
            ToolCallSummarizer.Summarize("http_request", P(("url", "https://example.com/api"), ("method", "get"))));

    [Fact]
    public void HttpRequest_PostShowsMethod()
        => Assert.Equal("Sending POST request to https://example.com/api",
            ToolCallSummarizer.Summarize("http_request", P(("url", "https://example.com/api"), ("method", "POST"))));

    [Fact]
    public void JsonProcessor_ShowsOperation()
        => Assert.Equal("Processing JSON (query_path)",
            ToolCallSummarizer.Summarize("json_processor", P(("operation", "query_path"))));

    // ---- Todos / code index ----

    [Fact]
    public void TodoManagement_ListReadsTodoList()
        => Assert.Equal("Reading todo list",
            ToolCallSummarizer.Summarize("todo_management", P(("action", "list"))));

    [Fact]
    public void TodoManagement_AddUpdatesTodoList()
        => Assert.Equal("Updating todo list",
            ToolCallSummarizer.Summarize("todo_management", P(("action", "add"))));

    [Fact]
    public void CodeIndex_WithQuery_ShowsSearchingCode()
        => Assert.Equal("Searching code for \"FeedView\"",
            ToolCallSummarizer.Summarize("code_index", P(("query", "FeedView"))));

    [Fact]
    public void CodeIndex_StructureQuery_ShowsProjectStructure()
        => Assert.Equal("Getting project structure",
            ToolCallSummarizer.Summarize("code_index", P(("query_type", "structure"))));

    // ---- Dataframe tools (all 28 registered ids) ----

    [Theory]
    [InlineData("dataframe_load_csv", "Loading CSV")]
    [InlineData("dataframe_load_json", "Loading JSON")]
    [InlineData("dataframe_load_parquet", "Loading Parquet")]
    [InlineData("dataframe_load_delta", "Loading Delta")]
    public void DataFrameLoad_ShowsFormatPathAndDataset(string tool, string expectedPrefix)
    {
        var summary = ToolCallSummarizer.Summarize(tool, P(("path", "data/sales.csv"), ("dataset_id", "sales")));
        Assert.Equal($"{expectedPrefix} data/sales.csv as sales", summary);
    }

    [Theory]
    [InlineData("dataframe_schema", "Inspecting schema of sales")]
    [InlineData("dataframe_profile", "Profiling sales")]
    [InlineData("dataframe_preview", "Previewing sales")]
    [InlineData("dataframe_value_counts", "Counting values in sales")]
    [InlineData("dataframe_assert", "Checking assertions on sales")]
    [InlineData("dataframe_select", "Selecting columns from sales")]
    [InlineData("dataframe_filter", "Filtering sales")]
    [InlineData("dataframe_with_column", "Adding column to sales")]
    [InlineData("dataframe_rename", "Renaming columns in sales")]
    [InlineData("dataframe_group_by", "Grouping sales")]
    [InlineData("dataframe_window", "Computing window functions on sales")]
    [InlineData("dataframe_pivot", "Pivoting sales")]
    [InlineData("dataframe_unpivot", "Unpivoting sales")]
    [InlineData("dataframe_unnest", "Unnesting sales")]
    [InlineData("dataframe_sample", "Sampling sales")]
    [InlineData("dataframe_sort", "Sorting sales")]
    [InlineData("dataframe_distinct", "Removing duplicates from sales")]
    [InlineData("dataframe_fillna", "Filling missing values in sales")]
    [InlineData("dataframe_dropna", "Dropping missing values from sales")]
    [InlineData("dataframe_drop", "Dropping dataset sales")]
    public void DataFrameDatasetTools_MentionDataset(string tool, string expected)
        => Assert.Equal(expected, ToolCallSummarizer.Summarize(tool, P(("dataset_id", "sales"))));

    [Fact]
    public void DataFrameList_ListsDatasets()
        => Assert.Equal("Listing datasets", ToolCallSummarizer.Summarize("dataframe_list", P()));

    [Fact]
    public void DataFrameUnion_CombinesDatasets()
        => Assert.Equal("Combining datasets", ToolCallSummarizer.Summarize("dataframe_union", P()));

    [Fact]
    public void DataFrameJoin_ShowsBothSides()
        => Assert.Equal("Joining sales with products",
            ToolCallSummarizer.Summarize("dataframe_join",
                P(("left_dataset_id", "sales"), ("right_dataset_id", "products"))));

    [Fact]
    public void DataFrameExport_ShowsDatasetAndPath()
        => Assert.Equal("Exporting sales to out.csv",
            ToolCallSummarizer.Summarize("dataframe_export", P(("dataset_id", "sales"), ("path", "out.csv"))));

    // ---- PDF tools (all 6 registered ids) ----

    [Theory]
    [InlineData("pdf_extract_text", "Extracting text from report.pdf")]
    [InlineData("pdf_extract_tables", "Extracting tables from report.pdf")]
    [InlineData("pdf_info", "Reading PDF info for report.pdf")]
    [InlineData("pdf_outline", "Reading outline of report.pdf")]
    [InlineData("pdf_reflow", "Extracting reading-order text from report.pdf")]
    public void PdfTools_MentionDocumentPath(string tool, string expected)
        => Assert.Equal(expected, ToolCallSummarizer.Summarize(tool, P(("path", "report.pdf"))));

    [Fact]
    public void PdfSearch_ShowsQuotedQuery()
        => Assert.Equal("Searching report.pdf for \"revenue\"",
            ToolCallSummarizer.Summarize("pdf_search", P(("path", "report.pdf"), ("query", "revenue"))));

    // ---- Catalog coverage: every registered tool id must have a specific summary,
    // never the generic humanized fallback of its own name. ----

    public static IEnumerable<object[]> AllRegisteredToolIds()
    {
        var ids = new[]
        {
            "copy_file", "delete_file", "list_directory", "move_file", "read_file", "write_file",
            "git_diff",
            "process_info", "system_info", "execute_command",
            "format_text", "replace_text", "search_text",
            "datetime_tool", "encoding_tool",
            "http_request", "json_processor",
            "todo_management",
            "create_directory", "code_index",
            "dataframe_load_csv", "dataframe_load_json", "dataframe_load_parquet", "dataframe_load_delta",
            "dataframe_schema", "dataframe_profile", "dataframe_preview", "dataframe_value_counts",
            "dataframe_assert", "dataframe_list", "dataframe_select", "dataframe_filter",
            "dataframe_with_column", "dataframe_rename", "dataframe_group_by", "dataframe_window",
            "dataframe_pivot", "dataframe_unpivot", "dataframe_unnest", "dataframe_join",
            "dataframe_sample", "dataframe_sort", "dataframe_distinct", "dataframe_union",
            "dataframe_fillna", "dataframe_dropna", "dataframe_export", "dataframe_drop",
            "pdf_extract_text", "pdf_extract_tables", "pdf_info", "pdf_outline", "pdf_reflow", "pdf_search",
        };
        foreach (var id in ids) yield return new object[] { id };
    }

    [Theory]
    [MemberData(nameof(AllRegisteredToolIds))]
    public void EveryRegisteredTool_HasNonFallbackSummary(string toolId)
    {
        var summary = ToolCallSummarizer.Summarize(toolId, new Dictionary<string, object?>());
        Assert.False(string.IsNullOrWhiteSpace(summary));
        // The generic fallback echoes the tool name as words; a covered tool must not do that.
        var words = string.Join(" ", toolId.Split('_'));
        var humanized = char.ToUpperInvariant(words[0]) + words.Substring(1);
        Assert.NotEqual(humanized, summary);
        // ASCII only, single line, never the raw snake_case id.
        Assert.DoesNotContain(toolId, summary);
        Assert.DoesNotContain("\n", summary);
        Assert.All(summary, c => Assert.True(c >= ' ' && c < 127, $"non-ASCII char in summary: {summary}"));
    }

    // ---- Fallback for unknown tools ----

    [Fact]
    public void UnknownTool_HumanizesNameAndShowsSalientArgument()
        => Assert.Equal("My custom tool: widget-42",
            ToolCallSummarizer.Summarize("my_custom_tool", P(("name", "widget-42"), ("verbose", "true"))));

    [Fact]
    public void UnknownTool_WithoutArguments_HumanizesNameOnly()
        => Assert.Equal("My custom tool", ToolCallSummarizer.Summarize("my_custom_tool", P()));

    [Fact]
    public void UnknownTool_NeverDumpsJsonArguments()
    {
        var summary = ToolCallSummarizer.Summarize("mystery_tool",
            P(("payload", "{\"a\":1,\"b\":[2,3]}")));
        Assert.DoesNotContain("{", summary);
        Assert.DoesNotContain("[", summary);
    }

    [Fact]
    public void UnknownTool_SkipsInternalDoubleUnderscoreParameters()
        => Assert.Equal("Mystery tool",
            ToolCallSummarizer.Summarize("mystery_tool", P(("__toolId", "mystery_tool_1"))));

    [Fact]
    public void NullToolName_ProducesGenericLabel()
        => Assert.Equal("Tool call", ToolCallSummarizer.Summarize(null, null));

    // ---- Execution-id suffix normalization ----

    [Fact]
    public void ExecutionIdSuffix_IsStripped()
        => Assert.Equal("Reading a.txt",
            ToolCallSummarizer.Summarize("read_file_1", P(("file_path", "a.txt"))));

    // ---- Path shortening ----

    [Fact]
    public void ShortenPath_MakesCwdRelative()
    {
        var cwd = Directory.GetCurrentDirectory();
        var full = Path.Combine(cwd, "src", "Program.cs");
        Assert.Equal(Path.Combine("src", "Program.cs"), ToolCallSummarizer.ShortenPath(full));
    }

    [Fact]
    public void ShortenPath_SqueezesVeryLongPaths()
    {
        var path = "/very/long/path/that/keeps/going/and/going/far/beyond/any/reasonable/display/width/file.cs";
        var shortened = ToolCallSummarizer.ShortenPath(path);
        Assert.Equal(".../width/file.cs", shortened);
    }

    [Fact]
    public void ShortenPath_LeavesShortRelativePathsAlone()
        => Assert.Equal("src/Foo.cs", ToolCallSummarizer.ShortenPath("src/Foo.cs"));
}
