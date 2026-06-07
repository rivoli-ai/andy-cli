using System.Collections.Generic;
using Andy.Cli.Input;
using Xunit;

namespace Andy.Cli.Tests.Input;

/// <summary>
/// Tests for <see cref="MouseReporting"/>, the SGR mouse-reporting toggle.
///
/// Mouse reporting suppresses the terminal's native click-drag text selection,
/// so the CLI keeps it off by default and only writes the enable/disable escape
/// sequences on an actual state change. These tests pin the exact init/teardown
/// sequences and the toggle/idempotency behavior using a fake write sink.
/// </summary>
public class MouseReportingTests
{
    private static MouseReporting NewWithSink(out List<string> writes)
    {
        var captured = new List<string>();
        writes = captured;
        return new MouseReporting(captured.Add);
    }

    [Fact]
    public void StartsDisabled_AndWritesNothing()
    {
        var m = NewWithSink(out var writes);

        Assert.False(m.Enabled);
        Assert.Empty(writes);
    }

    [Fact]
    public void Enable_WritesExactEnableSequence()
    {
        var m = NewWithSink(out var writes);

        bool now = m.Set(true);

        Assert.True(now);
        Assert.True(m.Enabled);
        Assert.Equal(new[] { MouseReporting.EnableSeq }, writes);
    }

    [Fact]
    public void EnableSequence_TurnsOnButtonTrackingAndSgrCoordinates()
    {
        // 1000h = button tracking, 1006h = SGR extended coordinates.
        Assert.Equal("\u001b[?1000h\u001b[?1006h", MouseReporting.EnableSeq);
    }

    [Fact]
    public void DisableSequence_IsTheInverseOfEnable()
    {
        // Same modes, terminated with 'l' (reset) instead of 'h' (set).
        Assert.Equal("\u001b[?1000l\u001b[?1006l", MouseReporting.DisableSeq);
        Assert.Equal(
            MouseReporting.EnableSeq.Replace('h', 'l'),
            MouseReporting.DisableSeq);
    }

    [Fact]
    public void EnableThenDisable_WritesBothSequencesInOrder()
    {
        var m = NewWithSink(out var writes);

        m.Set(true);
        bool now = m.Set(false);

        Assert.False(now);
        Assert.False(m.Enabled);
        Assert.Equal(new[] { MouseReporting.EnableSeq, MouseReporting.DisableSeq }, writes);
    }

    [Fact]
    public void SettingSameState_IsIdempotent_NoExtraWrites()
    {
        var m = NewWithSink(out var writes);

        m.Set(true);
        m.Set(true); // no-op
        Assert.Single(writes);

        m.Set(false);
        m.Set(false); // no-op
        Assert.Equal(2, writes.Count);
    }

    [Fact]
    public void DisableWhileAlreadyOff_WritesNothing()
    {
        var m = NewWithSink(out var writes);

        bool now = m.Set(false);

        Assert.False(now);
        Assert.Empty(writes);
    }

    [Fact]
    public void Toggle_FlipsStateAndEmitsMatchingSequences()
    {
        var m = NewWithSink(out var writes);

        Assert.True(m.Toggle());  // off -> on
        Assert.False(m.Toggle()); // on -> off
        Assert.True(m.Toggle());  // off -> on

        Assert.Equal(
            new[] { MouseReporting.EnableSeq, MouseReporting.DisableSeq, MouseReporting.EnableSeq },
            writes);
    }

    [Fact]
    public void FailedWrite_DoesNotThrow_ButStillAdvancesState()
    {
        var m = new MouseReporting(_ => throw new System.IO.IOException("terminal gone"));

        bool now = m.Set(true);

        Assert.True(now);
        Assert.True(m.Enabled);
    }
}
