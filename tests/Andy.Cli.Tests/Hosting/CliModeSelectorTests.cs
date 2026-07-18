using Andy.Cli.Hosting;
using Xunit;

namespace Andy.Cli.Tests.Hosting;

/// <summary>
/// Locks in the top-level mode-dispatch decision extracted from Program.Main.
/// The branch order is significant, so these cover the precedence cases
/// (bare "version" / "run" beating the generic non-dash command branch).
/// </summary>
public class CliModeSelectorTests
{
    [Fact]
    public void NoArgs_SelectsInteractive()
    {
        Assert.Equal(CliMode.Interactive, CliModeSelector.Select(new string[0]));
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("version")]
    public void VersionFlags_SelectVersion(string arg)
    {
        Assert.Equal(CliMode.Version, CliModeSelector.Select(new[] { arg }));
    }

    [Fact]
    public void AcpFlag_SelectsAcp()
    {
        Assert.Equal(CliMode.Acp, CliModeSelector.Select(new[] { "--acp" }));
    }

    [Fact]
    public void Run_SelectsHeadless()
    {
        Assert.Equal(CliMode.Headless, CliModeSelector.Select(new[] { "run", "--config", "x.json" }));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("tools")]
    [InlineData("permissions")]
    [InlineData("help")]
    public void NonDashFirstArg_SelectsCommand(string arg)
    {
        Assert.Equal(CliMode.Command, CliModeSelector.Select(new[] { arg }));
    }

    [Fact]
    public void UnknownDashArg_SelectsInteractive()
    {
        // An unrecognised flag (not a known mode selector) falls through to the
        // interactive TUI, matching the original Program.Main behaviour.
        Assert.Equal(CliMode.Interactive, CliModeSelector.Select(new[] { "--unknown" }));
    }

    [Fact]
    public void Run_TakesPrecedenceOverCommandBranch()
    {
        // "run" does not start with '-', so without ordered precedence it would
        // match the command branch. It must resolve to Headless instead.
        Assert.Equal(CliMode.Headless, CliModeSelector.Select(new[] { "run" }));
    }

    [Fact]
    public void BareVersionWord_TakesPrecedenceOverCommandBranch()
    {
        Assert.Equal(CliMode.Version, CliModeSelector.Select(new[] { "version" }));
    }
}
