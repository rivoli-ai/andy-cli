using Andy.Cli.Services;
using Andy.Cli.Services.Adapters;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// "show me the diffs" rendered the git_diff tool's raw output - emoji + "Change Summary" + markdown
/// bold - because the cleaning lived in a tracker path that the fragile per-call tracking ids could
/// bypass. ToolAdapter now cleans git_diff output at the source via LooksLikeGitDiffOutput +
/// ToolExecutionTracker.CleanGitDiff. These verify that detection + cleaning on the tool's real
/// output format produce a readable, ASCII diff with the hunks preserved.
/// </summary>
public sealed class GitDiffCleaningIntegrationTests
{
    // Representative of the real git_diff tool output (captured from the running tool): a
    // "Change Summary" header decorated with chart/page emoji and **markdown bold**, then ```diff
    // hunks with line numbers.
    private const string RealFormatRaw =
        "\U0001F4CA **Change Summary**\n\n" +
        "  `src/file.cs` (2 changes: **+1**, **-1**)\n\n" +
        "  **Total**: 1 file changed, 1 insertion(+), 1 deletion(-)\n\n" +
        "\U0001F4C4 **src/file.cs** (2 modifications)\n\n" +
        "  Lines 1-3:\n\n" +
        "```diff\n" +
        "   1: line one\n" +
        "-  2: line two\n" +
        "+  2: CHANGED LINE\n" +
        "   3: line three\n" +
        "```\n";

    [Fact]
    public void GitDiffOutput_IsDetected_AndCleanedPreservingHunks()
    {
        // ToolAdapter detects this as git_diff output...
        Assert.True(ToolAdapter.LooksLikeGitDiffOutput(RealFormatRaw));

        // ...and cleans it at the source.
        var cleaned = ToolExecutionTracker.CleanGitDiff(RealFormatRaw);

        Assert.DoesNotContain("\U0001F4CA", cleaned);          // chart emoji removed
        Assert.DoesNotContain("\U0001F4C4", cleaned);          // page emoji removed
        Assert.DoesNotContain("**", cleaned);                   // markdown bold removed
        Assert.DoesNotContain("Change Summary", cleaned);       // the noisy header is gone
        Assert.StartsWith("1 file changed", cleaned);           // the stat is promoted to the front
        Assert.Contains("+  2: CHANGED LINE", cleaned);         // the actual diff hunk is preserved
        Assert.Contains("-  2: line two", cleaned);             // including the removed line
    }

    [Theory]
    [InlineData("\U0001F4CA **Change Summary**\n  file (1 changes)", true)]
    [InlineData("```diff\n+ added\n```", true)]
    [InlineData("hello world\nexit 0", false)]
    [InlineData("Listed src: 3 items", false)]
    public void LooksLikeGitDiffOutput_DetectsOnlyGitDiffStyleOutput(string text, bool expected)
    {
        Assert.Equal(expected, ToolAdapter.LooksLikeGitDiffOutput(text));
    }
}
