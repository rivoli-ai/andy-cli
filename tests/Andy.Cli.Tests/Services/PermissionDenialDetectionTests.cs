using System.Collections.Generic;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Themes;
using Andy.Cli.Widgets.Tools;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// A permission denial and a tool failure used to look identical in the feed (#264), though they
/// mean different things: a denial is something the user can change their mind about.
/// </summary>
public class PermissionDenialDetectionTests
{
    [Fact]
    public void GateRefusalIsRecognizedAsADenial()
    {
        // The gate short-circuits without running the tool: a failure, no data, naming permission.
        var result = new ToolExecutionResult
        {
            IsSuccessful = false,
            ErrorMessage = "execute_command: permission denied by policy"
        };

        Assert.True(UiUpdatingToolExecutor.IsPermissionDenial(result));
    }

    [Fact]
    public void ToolLevelAccessDenialIsAnOrdinaryFailure()
    {
        // A tool refusing a path outside its permitted roots actually ran; it is not a gate denial.
        var result = new ToolExecutionResult
        {
            IsSuccessful = false,
            ErrorMessage = "Access denied: path is outside the permitted directory",
            Data = new Dictionary<string, object?> { ["path"] = "/etc/passwd" }
        };

        Assert.False(UiUpdatingToolExecutor.IsPermissionDenial(result));
    }

    [Fact]
    public void SuccessIsNeverADenial()
    {
        Assert.False(UiUpdatingToolExecutor.IsPermissionDenial(new ToolExecutionResult { IsSuccessful = true }));
    }

    [Fact]
    public void FailureWithoutAMessageIsNotADenial()
    {
        Assert.False(UiUpdatingToolExecutor.IsPermissionDenial(new ToolExecutionResult { IsSuccessful = false }));
    }

    [Theory]
    [InlineData(false, false, "x")]   // an ordinary failure
    [InlineData(true, false, "-")]    // denied
    [InlineData(false, true, "-")]    // cancelled
    public void StatusGlyphDistinguishesTheTerminalStates(bool denied, bool cancelled, string expectedGlyph)
    {
        var snapshot = new ToolCallSnapshot
        {
            ToolId = "read_file_1",
            ToolName = "read_file",
            IsComplete = true,
            IsSuccessful = false,
            WasDenied = denied,
            WasCancelled = cancelled
        };

        var item = new ToolCallItem(snapshot, ToolPresenterRegistry.Default.Resolve("read_file"));

        Assert.StartsWith(expectedGlyph, item.DebugRows(60)[0]);
    }

    [Fact]
    public void DeniedAndCancelledCallsExplainThemselves()
    {
        var presenter = ToolPresenterRegistry.Default.Resolve("read_file");
        var context = new ToolPresentationContext(80, false, Theme.Current);

        var denied = new ToolCallSnapshot
        {
            ToolId = "read_file_1",
            ToolName = "read_file",
            IsComplete = true,
            IsSuccessful = false,
            WasDenied = true
        };
        var cancelled = denied with { WasDenied = false, WasCancelled = true };

        Assert.Contains("permission", ToolPresenterHelpers.FailureText(denied), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Interrupted", ToolPresenterHelpers.FailureText(cancelled));
        Assert.NotEmpty(presenter.Present(denied, context).Body);
    }
}
