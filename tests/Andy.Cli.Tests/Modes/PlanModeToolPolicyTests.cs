using System.Collections.Generic;
using Andy.Cli.Modes;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// Classification rules for Plan mode (issue #278). The two properties that matter most are
/// covered explicitly: every mutating tool is denied, and anything the policy cannot classify -
/// MCP tools above all - is denied too.
/// </summary>
public class PlanModeToolPolicyTests
{
    private static readonly PlanModeToolPolicy Policy = PlanModeToolPolicy.Default;

    private static Dictionary<string, object?> P(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
        {
            d[key] = value;
        }

        return d;
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("delete_file")]
    [InlineData("move_file")]
    [InlineData("copy_file")]
    [InlineData("create_directory")]
    [InlineData("replace_text")]
    [InlineData("file_editor")]
    [InlineData("edit_file")]
    [InlineData("apply_patch")]
    [InlineData("execute_command")]
    [InlineData("bash")]
    [InlineData("dataframe_export")]
    [InlineData("todo_management")]
    public void EveryKnownMutatingTool_IsDenied(string toolId)
    {
        var verdict = Policy.Evaluate(toolId, P());

        Assert.False(verdict.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        Assert.Contains("Plan mode", verdict.Reason!);
    }

    [Fact]
    public void EveryToolOnTheMutatingList_IsDenied()
    {
        // Guards against a future edit adding an id to the mutating table without the deny path
        // actually covering it.
        foreach (var toolId in PlanModeToolPolicy.KnownMutatingToolIds)
        {
            Assert.False(Policy.Evaluate(toolId, P()).Allowed);
        }
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("read_many_files")]
    [InlineData("list_directory")]
    [InlineData("search_text")]
    [InlineData("search_files")]
    [InlineData("git_diff")]
    [InlineData("git_log")]
    [InlineData("code_index")]
    [InlineData("system_info")]
    [InlineData("json_processor")]
    [InlineData("pdf_extract_text")]
    [InlineData("dataframe_preview")]
    [InlineData("skill")]
    public void ReadOnlyTools_AreAllowed(string toolId)
    {
        var verdict = Policy.Evaluate(toolId, P());

        Assert.True(verdict.Allowed, verdict.Reason);
        Assert.Null(verdict.Reason);
    }

    [Fact]
    public void EveryToolOnTheReadOnlyList_IsAllowed()
    {
        foreach (var toolId in PlanModeToolPolicy.BuiltInReadOnlyToolIds)
        {
            Assert.True(Policy.Evaluate(toolId, P()).Allowed, toolId);
        }
    }

    [Fact]
    public void ExecuteCommand_IsDeniedEvenForAnObviouslyReadOnlyCommand()
    {
        // Plan mode denies the shell wholesale: classifying arbitrary command lines as safe is
        // exactly the analysis this feature refuses to bet on.
        var verdict = Policy.Evaluate("execute_command", P(("command", "ls -la")));

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void ExecuteCommand_IsDeniedForAnIndirectWrite()
    {
        var verdict = Policy.Evaluate("execute_command", P(("command", "echo hi > /tmp/andy-plan-test")));

        Assert.False(verdict.Allowed);
    }

    [Theory]
    [InlineData("format_on_save")]
    [InlineData("run_formatter")]
    [InlineData("post_edit_hook")]
    public void FormatterStyleHooks_AreDenied_BecauseTheyAreUnclassified(string toolId)
    {
        // A future formatter/post-edit hook is just another tool id. Plan mode's fail-closed default
        // covers it without needing to know it exists, and the shell route it would otherwise take
        // (execute_command) is denied outright.
        Assert.False(Policy.Evaluate(toolId, P()).Allowed);
        Assert.False(Policy.Evaluate("execute_command", P(("command", "dotnet format"))).Allowed);
    }

    [Theory]
    [InlineData("mcp__github__create_issue")]
    [InlineData("mcp__docs__search")]
    [InlineData("some_cli_tool")]
    [InlineData("brand_new_tool_from_a_package_bump")]
    public void UnclassifiedTools_FailClosed(string toolId)
    {
        var verdict = Policy.Evaluate(toolId, P());

        Assert.False(verdict.Allowed);
        Assert.Contains("not on the read-only tool list", verdict.Reason!);
    }

    [Fact]
    public void EmptyToolId_FailsClosed()
    {
        Assert.False(Policy.Evaluate("", null).Allowed);
        Assert.False(Policy.Evaluate("   ", null).Allowed);
    }

    [Fact]
    public void OptedInReadOnlyTools_AreAllowed()
    {
        var policy = new PlanModeToolPolicy(new[] { "mcp__docs__search" });

        Assert.True(policy.Evaluate("mcp__docs__search", P()).Allowed);
        // The opt-in is per tool id and never widens to a neighbour.
        Assert.False(policy.Evaluate("mcp__docs__write", P()).Allowed);
    }

    [Fact]
    public void OptInCannotReEnableAKnownMutatingTool()
    {
        // An operator asserting that write_file is read-only is wrong; the built-in classification
        // wins so a config file can never punch a hole in the overlay.
        var policy = new PlanModeToolPolicy(new[] { "write_file", "execute_command" });

        Assert.False(policy.Evaluate("write_file", P()).Allowed);
        Assert.False(policy.Evaluate("execute_command", P()).Allowed);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("get")]
    [InlineData("HEAD")]
    [InlineData(null)]
    public void HttpRequest_IsAllowedForSafeMethods(string? method)
    {
        var parameters = method is null
            ? P(("url", "https://example.com"))
            : P(("url", "https://example.com"), ("method", method));

        Assert.True(Policy.Evaluate("http_request", parameters).Allowed);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("put")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("SOMETHING_ELSE")]
    public void HttpRequest_IsDeniedForStateChangingMethods(string method)
    {
        var verdict = Policy.Evaluate("http_request", P(("url", "https://example.com"), ("method", method)));

        Assert.False(verdict.Allowed);
        Assert.Contains("remote state", verdict.Reason!);
    }

    [Fact]
    public void AnOtherwiseReadOnlyCall_IsDeniedWhenItWritesItsOutputToAFile()
    {
        // Indirect mutation: a read tool asked to persist its result is a write.
        var verdict = Policy.Evaluate(
            "http_request",
            P(("url", "https://example.com"), ("method", "GET"), ("output_file", "/tmp/andy-plan-out.txt")));

        Assert.False(verdict.Allowed);
        Assert.Contains("output_file", verdict.Reason!);
    }

    [Fact]
    public void OutputPathDetection_IsCaseInsensitive()
    {
        var verdict = Policy.Evaluate("read_file", P(("File_Path", "a.txt"), ("Output_Path", "b.txt")));

        Assert.False(verdict.Allowed);
    }
}
