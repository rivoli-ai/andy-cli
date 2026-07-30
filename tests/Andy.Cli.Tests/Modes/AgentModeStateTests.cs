using System.Collections.Generic;
using Andy.Cli.Modes;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// Transition rules for the shared mode state (issue #278): Plan can be entered from anywhere, but
/// leaving it requires an explicit user action.
/// </summary>
public class AgentModeStateTests
{
    [Fact]
    public void DefaultsToBuild()
    {
        var state = new AgentModeState();
        Assert.Equal(AgentMode.Build, state.Current);
        Assert.False(state.IsReadOnly);
    }

    [Theory]
    [InlineData(ModeChangeSource.Startup)]
    [InlineData(ModeChangeSource.UserCommand)]
    [InlineData(ModeChangeSource.SessionRestore)]
    [InlineData(ModeChangeSource.HeadlessConfig)]
    public void EnteringPlan_IsAllowedFromEverySource(ModeChangeSource source)
    {
        var state = new AgentModeState();

        Assert.True(state.TrySet(AgentMode.Plan, source, out var error));
        Assert.Null(error);
        Assert.Equal(AgentMode.Plan, state.Current);
        Assert.True(state.IsReadOnly);
    }

    [Theory]
    [InlineData(ModeChangeSource.Startup)]
    [InlineData(ModeChangeSource.SessionRestore)]
    [InlineData(ModeChangeSource.HeadlessConfig)]
    public void LeavingPlan_IsRefused_ForEverySourceExceptTheUser(ModeChangeSource source)
    {
        var state = new AgentModeState(AgentMode.Plan);

        Assert.False(state.TrySet(AgentMode.Build, source, out var error));
        Assert.NotNull(error);
        Assert.Contains("/mode build", error!);
        Assert.Equal(AgentMode.Plan, state.Current);
    }

    [Fact]
    public void LeavingPlan_SucceedsForAnExplicitUserCommand()
    {
        var state = new AgentModeState(AgentMode.Plan);

        Assert.True(state.TrySet(AgentMode.Build, ModeChangeSource.UserCommand, out var error));
        Assert.Null(error);
        Assert.Equal(AgentMode.Build, state.Current);
    }

    [Fact]
    public void ModeChanged_FiresOnceWithBothEnds()
    {
        var state = new AgentModeState();
        var events = new List<AgentModeChangedEventArgs>();
        state.ModeChanged += (_, e) => events.Add(e);

        state.TrySet(AgentMode.Plan, ModeChangeSource.UserCommand, out _);

        var e = Assert.Single(events);
        Assert.Equal(AgentMode.Build, e.Previous.Mode);
        Assert.Equal(AgentMode.Plan, e.Current.Mode);
        Assert.Equal(ModeChangeSource.UserCommand, e.Source);
    }

    [Fact]
    public void SettingTheSameMode_IsANoOpAndRaisesNothing()
    {
        var state = new AgentModeState(AgentMode.Plan);
        var fired = 0;
        state.ModeChanged += (_, _) => fired++;

        Assert.True(state.TrySet(AgentMode.Plan, ModeChangeSource.SessionRestore, out var error));
        Assert.Null(error);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void RefusedTransition_RaisesNoEvent()
    {
        var state = new AgentModeState(AgentMode.Plan);
        var fired = 0;
        state.ModeChanged += (_, _) => fired++;

        Assert.False(state.TrySet(AgentMode.Build, ModeChangeSource.SessionRestore, out _));
        Assert.Equal(0, fired);
    }
}
