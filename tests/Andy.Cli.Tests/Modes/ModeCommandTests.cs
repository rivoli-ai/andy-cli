using System;
using Andy.Cli.Commands;
using Andy.Cli.Modes;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// <c>/mode</c> behaviour (issue #278), including the fail-closed rejection of unknown modes.
/// </summary>
public class ModeCommandTests
{
    [Fact]
    public void NoArguments_ReportsTheCurrentModeAndTheAvailableOnes()
    {
        var command = new ModeCommand(new AgentModeState(AgentMode.Plan));

        var result = command.Execute(Array.Empty<string>());

        Assert.True(result.Success);
        Assert.Contains("Current mode: Plan", result.Message);
        Assert.Contains("/mode build", result.Message);
        Assert.Contains("/mode plan", result.Message);
    }

    [Fact]
    public void SwitchingToPlan_Succeeds()
    {
        var state = new AgentModeState();
        var command = new ModeCommand(state);

        var result = command.Execute(new[] { "plan" });

        Assert.True(result.Success);
        Assert.Equal(AgentMode.Plan, state.Current);
        Assert.Contains("Plan", result.Message);
    }

    [Fact]
    public void SwitchingBackToBuild_Succeeds_BecauseTheCommandIsAnExplicitUserAction()
    {
        var state = new AgentModeState(AgentMode.Plan);
        var command = new ModeCommand(state);

        var result = command.Execute(new[] { "build" });

        Assert.True(result.Success);
        Assert.Equal(AgentMode.Build, state.Current);
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("readonly")]
    [InlineData("bui1d")]
    public void UnknownMode_IsRejectedAndLeavesTheModeAlone(string requested)
    {
        var state = new AgentModeState(AgentMode.Plan);
        var command = new ModeCommand(state);

        var result = command.Execute(new[] { requested });

        Assert.False(result.Success);
        Assert.Contains("Unknown mode", result.Message);
        Assert.Equal(AgentMode.Plan, state.Current);
    }

    [Fact]
    public void RepeatingTheCurrentMode_IsAcknowledgedNotTreatedAsAChange()
    {
        var state = new AgentModeState(AgentMode.Plan);
        var command = new ModeCommand(state);

        var result = command.Execute(new[] { "plan" });

        Assert.True(result.Success);
        Assert.Contains("Already in Plan mode", result.Message);
    }

    [Fact]
    public void CommandMetadata_MatchesTheSlashCatalogEntry()
    {
        var command = new ModeCommand(new AgentModeState());

        Assert.Equal("mode", command.Name);
        Assert.Empty(command.Aliases);
    }
}
