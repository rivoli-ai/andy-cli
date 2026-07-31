using Andy.Cli.HeadlessConfig;
using Andy.Cli.Modes;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// Headless mode selection (issue #278): <c>--mode</c> is explicit and fails closed. An
/// unrecognized value must abort the run with a config error, never fall back to Build.
/// </summary>
public class HeadlessModeSelectionTests
{
    [Fact]
    public void NoModeFlag_DefaultsToBuild()
    {
        var parsed = HeadlessRunner.ParseArgsForTest(new[] { "run", "--headless", "--config", "c.json" });

        Assert.Null(parsed.Error);
        Assert.Equal(AgentMode.Build, parsed.Mode);
    }

    [Fact]
    public void PlanMode_IsAccepted()
    {
        var parsed = HeadlessRunner.ParseArgsForTest(
            new[] { "run", "--headless", "--config", "c.json", "--mode", "plan" });

        Assert.Null(parsed.Error);
        Assert.Equal(AgentMode.Plan, parsed.Mode);
    }

    [Fact]
    public void BuildMode_IsAccepted()
    {
        var parsed = HeadlessRunner.ParseArgsForTest(
            new[] { "run", "--headless", "--config", "c.json", "--mode", "BUILD" });

        Assert.Null(parsed.Error);
        Assert.Equal(AgentMode.Build, parsed.Mode);
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("yolo")]
    [InlineData("readonly")]
    [InlineData("")]
    public void UnknownMode_IsAConfigError(string requested)
    {
        var parsed = HeadlessRunner.ParseArgsForTest(
            new[] { "run", "--headless", "--config", "c.json", "--mode", requested });

        Assert.NotNull(parsed.Error);
        Assert.Contains("Unknown mode", parsed.Error!);
    }

    [Fact]
    public void MissingModeArgument_IsAConfigError()
    {
        var parsed = HeadlessRunner.ParseArgsForTest(
            new[] { "run", "--headless", "--config", "c.json", "--mode" });

        Assert.NotNull(parsed.Error);
        Assert.Contains("--mode", parsed.Error!);
    }

    [Fact]
    public async System.Threading.Tasks.Task UnknownMode_ExitsWithConfigError()
    {
        // The whole runner, not just the parser: an unknown mode never reaches the agent loop.
        using var stdout = new System.IO.StringWriter();
        using var stderr = new System.IO.StringWriter();

        var exit = await HeadlessRunner.RunAsync(
            new[] { "run", "--headless", "--config", "does-not-matter.json", "--mode", "planning" },
            stdout,
            stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, exit);
        Assert.Contains("Unknown mode", stderr.ToString());
    }

    [Fact]
    public void StartupSelector_DefaultsToBuild_WhenNoFlagIsPresent()
    {
        var selection = StartupModeSelector.Resolve(new[] { "--auto" });

        Assert.Null(selection.Error);
        Assert.Equal(AgentMode.Build, selection.Mode);
    }

    [Fact]
    public void StartupSelector_AcceptsTheSeparatedFlagForm()
    {
        var selection = StartupModeSelector.Resolve(new[] { "--mode", "plan" });

        Assert.Null(selection.Error);
        Assert.Equal(AgentMode.Plan, selection.Mode);
    }

    [Fact]
    public void StartupSelector_AcceptsTheEqualsFlagForm()
    {
        var selection = StartupModeSelector.Resolve(new[] { "--mode=plan" });

        Assert.Null(selection.Error);
        Assert.Equal(AgentMode.Plan, selection.Mode);
    }

    [Fact]
    public void StartupSelector_FallsBackToTheMostRestrictiveMode_OnAnUnknownValue()
    {
        // Fail closed: a typo must never leave the session in the permissive default.
        var selection = StartupModeSelector.Resolve(new[] { "--mode", "planning" });

        Assert.NotNull(selection.Error);
        Assert.Equal(StartupModeSelector.SafestMode, selection.Mode);
        Assert.Equal(AgentMode.Plan, selection.Mode);
    }

    [Fact]
    public void StartupSelector_FailsClosed_WhenTheValueIsMissing()
    {
        var selection = StartupModeSelector.Resolve(new[] { "--mode" });

        Assert.NotNull(selection.Error);
        Assert.Equal(AgentMode.Plan, selection.Mode);
    }
}
