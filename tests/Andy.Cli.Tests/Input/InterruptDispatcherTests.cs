using System;
using Andy.Cli.Input;
using Xunit;

namespace Andy.Cli.Tests.Input;

/// <summary>
/// Ctrl+C routing for shell escape (issue #286). The dispatcher decides whether a SIGINT is
/// consumed by in-flight work or falls through to the terminal-restoring, process-terminating
/// default. Getting this wrong in either direction is bad: an over-eager claim makes andy-cli
/// unkillable, and a missed claim tears down the TUI mid-command.
/// </summary>
public class InterruptDispatcherTests
{
    [Fact]
    public void Dispatch_WithNothingArmed_DoesNotConsumeThePress()
    {
        var dispatcher = new InterruptDispatcher();

        Assert.False(dispatcher.IsArmed);
        Assert.False(dispatcher.Dispatch());
    }

    [Fact]
    public void Dispatch_WithAHandlerArmed_InvokesItAndConsumesThePress()
    {
        var dispatcher = new InterruptDispatcher();
        var calls = 0;

        using (dispatcher.Install(() => { calls++; return true; }))
        {
            Assert.True(dispatcher.IsArmed);
            Assert.True(dispatcher.Dispatch());
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_RestoresTheUnarmedState()
    {
        var dispatcher = new InterruptDispatcher();

        var registration = dispatcher.Install(() => true);
        registration.Dispose();

        Assert.False(dispatcher.IsArmed);
        Assert.False(dispatcher.Dispatch());
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var dispatcher = new InterruptDispatcher();
        var registration = dispatcher.Install(() => true);

        registration.Dispose();
        registration.Dispose();

        Assert.False(dispatcher.IsArmed);
    }

    [Fact]
    public void NestedInstall_RestoresThePreviousHandlerOnDispose()
    {
        // A shell command started while some other component holds Ctrl+C must not strand the
        // dispatcher: disposing the inner claim hands the key back, it does not disarm entirely.
        var dispatcher = new InterruptDispatcher();
        var outer = 0;
        var inner = 0;

        using (dispatcher.Install(() => { outer++; return true; }))
        {
            using (dispatcher.Install(() => { inner++; return true; }))
            {
                dispatcher.Dispatch();
            }

            dispatcher.Dispatch();
        }

        Assert.Equal(1, inner);
        Assert.Equal(1, outer);
        Assert.False(dispatcher.IsArmed);
    }

    [Fact]
    public void Handler_ReturningFalse_LeavesThePressToTheDefaultBehaviour()
    {
        var dispatcher = new InterruptDispatcher();

        using var _ = dispatcher.Install(() => false);

        Assert.False(dispatcher.Dispatch());
    }

    [Fact]
    public void Handler_ThatThrows_DegradesToTheDefaultBehaviour()
    {
        // A bug in a handler must never make the app impossible to interrupt.
        var dispatcher = new InterruptDispatcher();

        using var _ = dispatcher.Install(() => throw new InvalidOperationException("boom"));

        Assert.False(dispatcher.Dispatch());
    }

    [Fact]
    public void Install_RejectsANullHandler()
    {
        var dispatcher = new InterruptDispatcher();

        Assert.Throws<ArgumentNullException>(() => dispatcher.Install(null!));
    }

    [Fact]
    public void Instance_IsSharedAndStartsUnarmed()
    {
        // The terminal input layer and the interactive loop both consult this one instance.
        Assert.Same(InterruptDispatcher.Instance, InterruptDispatcher.Instance);
        Assert.False(InterruptDispatcher.Instance.IsArmed);
    }
}
