using System.Collections.Generic;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Verifies <see cref="ToolExecutionTracker"/> formats tool results richly (issue #135):
///  - execute_command surfaces stdout AND stderr, plus the exit code on failure;
///  - git_diff output is cleaned of emoji/markdown decoration, leads with the change stat,
///    and de-duplicates the repeated file block while keeping the real diff hunks.
/// </summary>
public class ToolOutputFormattingTests
{
    [Fact]
    public void CommandOutput_Success_ShowsStdout_NoExitPrefix()
    {
        var d = new Dictionary<string, object?>
        {
            ["exit_code"] = 0,
            ["stdout"] = "hello world\nsecond line\n",
            ["stderr"] = "",
        };
        var s = ToolExecutionTracker.FormatCommandOutput(d);
        Assert.Equal("hello world\nsecond line", s);
        Assert.DoesNotContain("exit", s);
    }

    [Fact]
    public void CommandOutput_Failure_ShowsExitCodeAndStderr()
    {
        var d = new Dictionary<string, object?>
        {
            ["exit_code"] = 1,
            ["stdout"] = "",
            ["stderr"] = "ls: /nope: No such file or directory\n",
        };
        var s = ToolExecutionTracker.FormatCommandOutput(d);
        Assert.Contains("exit 1", s);
        Assert.Contains("No such file or directory", s);
    }

    [Fact]
    public void CommandOutput_ShowsBothStdoutAndStderr()
    {
        var d = new Dictionary<string, object?>
        {
            ["exit_code"] = 0,
            ["stdout"] = "out line",
            ["stderr"] = "warn line",
        };
        var s = ToolExecutionTracker.FormatCommandOutput(d);
        Assert.Contains("out line", s);
        Assert.Contains("warn line", s);
    }

    private const string SampleGitDiff =
        "\U0001F4CA **Change Summary**\n" +
        "\n" +
        "  `file.csproj` (1 changes: **+1**)\n" +
        "\n" +
        "  **Total**: 1 file changed, 1 insertion(+)\n" +
        "\n" +
        "\U0001F4C4 **file.csproj** (1 modifications)\n" +
        "   **+1** additions\n" +
        "\n" +
        "  Lines 116-118:\n" +
        "\n" +
        "```diff\n" +
        "   116:     <Compile Include=\"A.cs\" />\n" +
        "+  117:     <Compile Include=\"New.cs\" />\n" +
        "   118:     <Compile Include=\"B.cs\" />\n" +
        "```\n" +
        "\n" +
        "\U0001F4C4 **file.csproj** (1 modifications)\n" +   // duplicate file block (tool quirk)
        "   **+1** additions\n" +
        "```diff\n" +
        "   116:     <Compile Include=\"A.cs\" />\n" +
        "+  117:     <Compile Include=\"New.cs\" />\n" +
        "   118:     <Compile Include=\"B.cs\" />\n" +
        "```\n";

    [Fact]
    public void GitDiff_IsCleanedOfEmojiAndMarkdown()
    {
        var s = ToolExecutionTracker.CleanGitDiff(SampleGitDiff);
        Assert.DoesNotContain("\U0001F4CA", s);   // chart emoji gone
        Assert.DoesNotContain("\U0001F4C4", s);   // page emoji gone
        Assert.DoesNotContain("**", s);            // markdown bold markers gone
    }

    [Fact]
    public void GitDiff_LeadsWithChangeStat()
    {
        var s = ToolExecutionTracker.CleanGitDiff(SampleGitDiff);
        var first = s.Split('\n')[0];
        Assert.Contains("1 file changed", first);
        Assert.DoesNotContain("Total:", first);
    }

    [Fact]
    public void GitDiff_KeepsHunksButDeDuplicatesRepeatedBlock()
    {
        var s = ToolExecutionTracker.CleanGitDiff(SampleGitDiff);
        Assert.Contains("+  117:     <Compile Include=\"New.cs\" />", s);
        // The added line appears exactly once even though the source repeated the whole block.
        var occurrences = s.Split(new[] { "+  117:" }, System.StringSplitOptions.None).Length - 1;
        Assert.Equal(1, occurrences);
    }
}
