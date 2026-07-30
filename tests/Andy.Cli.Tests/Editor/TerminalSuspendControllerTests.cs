using System;
using System.Collections.Generic;
using Andy.Cli.Editor;
using Xunit;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// Terminal hand-off and restoration (issue #287) - the highest-risk part of the feature.
/// These tests pin the exact escape sequences and the recovery behaviour on the success,
/// failure, Ctrl+C and process-exit paths using a fake write sink and a fake raw input.
/// </summary>
public class TerminalSuspendControllerTests
{
    private sealed class FakeInput : ISuspendableTerminalInput
    {
        public int SuspendCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public bool IsSuspended => SuspendCalls > ResumeCalls;

        public IDisposable Suspend()
        {
            SuspendCalls++;
            return new Scope(this);
        }

        private sealed class Scope : IDisposable
        {
            private readonly FakeInput _owner;
            private bool _done;
            public Scope(FakeInput owner) => _owner = owner;
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _owner.ResumeCalls++;
            }
        }
    }

    private sealed class ThrowingInput : ISuspendableTerminalInput
    {
        public IDisposable Suspend() => throw new InvalidOperationException("stty is gone");
    }

    private static TerminalSuspendController New(
        out List<string> writes,
        out FakeInput input,
        out List<int> repaints)
    {
        var w = new List<string>();
        var i = new FakeInput();
        var r = new List<int>();
        writes = w;
        input = i;
        repaints = r;
        return new TerminalSuspendController(w.Add, i, () => r.Add(1));
    }

    [Fact]
    public void Suspend_LeavesTheTuiInTheDocumentedOrder()
    {
        var c = New(out var writes, out var input, out _);

        c.Suspend();

        // Mouse off, wrap on, cursor visible, alternate screen left.
        Assert.Equal(new[] { TerminalSuspendController.LeaveTuiSequence }, writes);
        Assert.Contains("[?1000l", writes[0]);
        Assert.Contains("[?1006l", writes[0]);
        Assert.Contains("[?7h", writes[0]);
        Assert.Contains("[?25h", writes[0]);
        Assert.Contains("[?1049l", writes[0]);
        Assert.True(input.IsSuspended);
        Assert.True(c.IsSuspended);
    }

    [Fact]
    public void Dispose_RestoresRawModeAltScreenCursorMouseAndRepaints()
    {
        var c = New(out var writes, out var input, out var repaints);

        var scope = c.Suspend();
        scope.Dispose();

        Assert.Equal(
            new[] { TerminalSuspendController.LeaveTuiSequence, TerminalSuspendController.EnterTuiSequence },
            writes);
        Assert.Contains("[?1049h", writes[1]); // back into the alternate screen
        Assert.Contains("[?25l", writes[1]);   // TUI owns the cursor again
        Assert.Contains("[?7l", writes[1]);    // wrap off again
        Assert.False(input.IsSuspended);       // raw mode + mouse state restored
        Assert.Single(repaints);               // full repaint requested
        Assert.False(c.IsSuspended);
        Assert.True(scope.Completed);
    }

    [Fact]
    public void Restore_RunsOnlyOnce_EvenWhenDisposedRepeatedly()
    {
        var c = New(out var writes, out var input, out var repaints);

        var scope = c.Suspend();
        scope.Dispose();
        scope.Dispose();
        scope.Dispose();

        Assert.Equal(2, writes.Count);
        Assert.Single(repaints);
        Assert.False(input.IsSuspended);
    }

    [Fact]
    public void SuspendWhileAlreadySuspended_ReturnsTheSameScope()
    {
        var c = New(out var writes, out var input, out _);

        var first = c.Suspend();
        var second = c.Suspend();

        Assert.Same(first, second);
        Assert.Single(writes);
        Assert.Equal(1, input.SuspendCalls);
    }

    [Fact]
    public void SuspendAfterRestore_StartsAFreshHandOff()
    {
        var c = New(out var writes, out var input, out _);

        c.Suspend().Dispose();
        var second = c.Suspend();

        Assert.Equal(3, writes.Count);
        Assert.Equal(2, input.SuspendCalls);
        second.Dispose();
        Assert.False(input.IsSuspended);
    }

    [Fact]
    public void FailingInputSuspend_StillHandsOverAndStillRestores()
    {
        var writes = new List<string>();
        int repaints = 0;
        var c = new TerminalSuspendController(writes.Add, new ThrowingInput(), () => repaints++);

        var scope = c.Suspend();
        scope.Dispose();

        Assert.Equal(
            new[] { TerminalSuspendController.LeaveTuiSequence, TerminalSuspendController.EnterTuiSequence },
            writes);
        Assert.Equal(1, repaints);
    }

    [Fact]
    public void FailingWrites_DoNotPreventRestoreOrRepaint()
    {
        int repaints = 0;
        var input = new FakeInput();
        var c = new TerminalSuspendController(_ => throw new IO_Error(), input, () => repaints++);

        var scope = c.Suspend();
        scope.Dispose();

        Assert.False(input.IsSuspended);
        Assert.Equal(1, repaints);
    }

    private sealed class IO_Error : Exception { }

    [Fact]
    public void FailingRepaintCallback_DoesNotEscape()
    {
        var writes = new List<string>();
        var input = new FakeInput();
        var c = new TerminalSuspendController(writes.Add, input, () => throw new InvalidOperationException());

        c.Suspend().Dispose();

        Assert.False(input.IsSuspended);
        Assert.Equal(2, writes.Count);
    }

    // ----- Ctrl+C -----

    [Fact]
    public void CtrlC_WhileTheEditorOwnsTheTerminal_IsAbsorbed_AndTheNormalRestoreStillRuns()
    {
        // Both Andy and the editor receive the SIGINT. Killing Andy would strand the
        // terminal, so the hand-off cancels the default termination; when the editor
        // exits (typically 130) the normal restore path runs and everything recovers.
        var c = New(out var writes, out var input, out var repaints);

        var scope = c.Suspend();
        Assert.True(scope.SuppressCancel());
        Assert.True(scope.CancelRequested);
        Assert.False(scope.Completed);

        scope.Dispose();

        Assert.Equal(
            new[] { TerminalSuspendController.LeaveTuiSequence, TerminalSuspendController.EnterTuiSequence },
            writes);
        Assert.False(input.IsSuspended);
        Assert.Single(repaints);
    }

    [Fact]
    public void CtrlC_AfterTheHandOffEnded_IsNotAbsorbed()
    {
        var c = New(out _, out _, out _);

        var scope = c.Suspend();
        scope.Dispose();

        Assert.False(scope.SuppressCancel());
    }

    // ----- process exit / signal death -----

    [Fact]
    public void Abandon_LeavesAUsableTerminal_WithoutReenteringTheAlternateScreen()
    {
        var c = New(out var writes, out var input, out var repaints);

        var scope = c.Suspend();
        scope.Abandon();

        Assert.Equal(
            new[] { TerminalSuspendController.LeaveTuiSequence, TerminalSuspendController.EmergencySequence },
            writes);
        Assert.DoesNotContain("[?1049h", writes[1]);
        Assert.Contains("[?25h", writes[1]); // cursor visible
        Assert.Contains("[?7h", writes[1]);  // wrapping on
        Assert.Contains("[?1000l", writes[1]); // mouse reporting off
        Assert.False(input.IsSuspended);
        Assert.Empty(repaints); // no point repainting a TUI that is going away
        Assert.True(scope.Completed);
    }

    [Fact]
    public void Abandon_IsIdempotent_AndSuppressesALaterDispose()
    {
        var c = New(out var writes, out var input, out var repaints);

        var scope = c.Suspend();
        scope.Abandon();
        scope.Abandon();
        scope.Dispose();

        Assert.Equal(2, writes.Count);
        Assert.Empty(repaints);
        Assert.False(input.IsSuspended);
    }

    [Fact]
    public void DisposeThenAbandon_KeepsTheNormalRestore()
    {
        var c = New(out var writes, out _, out var repaints);

        var scope = c.Suspend();
        scope.Dispose();
        scope.Abandon();

        Assert.Equal(
            new[] { TerminalSuspendController.LeaveTuiSequence, TerminalSuspendController.EnterTuiSequence },
            writes);
        Assert.Single(repaints);
    }

    [Fact]
    public void NoInput_IsSupported()
    {
        var writes = new List<string>();
        var c = new TerminalSuspendController(writes.Add);

        c.Suspend().Dispose();

        Assert.Equal(2, writes.Count);
    }
}
