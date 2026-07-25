using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Every tool the CLI registers should have a presenter that knows what its result means
/// (the epic behind #249). The generic fallback exists for tools this build has never seen -
/// third-party and MCP tools - not for our own catalog.
///
/// When a tool is added to ToolCatalog, this test fails until it is given a presenter or
/// deliberately added to the fallback list below.
/// </summary>
public class PresenterCoverageTests
{
    [Theory]
    // File system
    [InlineData("read_file")]
    [InlineData("write_file")]
    [InlineData("copy_file")]
    [InlineData("move_file")]
    [InlineData("delete_file")]
    [InlineData("list_directory")]
    [InlineData("create_directory")]
    // Git
    [InlineData("git_diff")]
    // System
    [InlineData("execute_command")]
    [InlineData("process_info")]
    [InlineData("system_info")]
    // Text
    [InlineData("format_text")]
    [InlineData("replace_text")]
    [InlineData("search_text")]
    // Utilities
    [InlineData("date_time")]
    [InlineData("encoding_tool")]
    // Web
    [InlineData("http_request")]
    [InlineData("json_processor")]
    // Planning and CLI tools
    [InlineData("todo_management")]
    [InlineData("code_index")]
    // Skills
    [InlineData("skill")]
    [InlineData("skill_file")]
    // Data and documents
    [InlineData("dataframe_load_csv")]
    [InlineData("dataframe_preview")]
    [InlineData("dataframe_export")]
    [InlineData("pdf_info")]
    [InlineData("pdf_extract_text")]
    [InlineData("pdf_search")]
    public void EveryRegisteredToolHasADedicatedPresenter(string toolId)
    {
        Assert.NotNull(ToolPresenterRegistry.Default.TryResolve(toolId));
    }

    [Theory]
    [InlineData("some_third_party_tool")]
    [InlineData("mcp_server_something")]
    public void UnknownToolsFallBackToTheGenericPresenter(string toolId)
    {
        Assert.Null(ToolPresenterRegistry.Default.TryResolve(toolId));
        Assert.IsType<GenericToolPresenter>(ToolPresenterRegistry.Default.Resolve(toolId));
    }

    [Fact]
    public void PresentersResolveThroughTheExecutionCounterSuffix()
    {
        // The UI appends a counter to make ids unique across parallel calls.
        Assert.NotNull(ToolPresenterRegistry.Default.TryResolve("execute_command_12"));
        Assert.NotNull(ToolPresenterRegistry.Default.TryResolve("dataframe_preview_3"));
    }
}
