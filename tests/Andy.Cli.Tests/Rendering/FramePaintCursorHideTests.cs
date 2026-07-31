using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Rendering;
using Xunit;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Rendering;

/// <summary>
/// Pins the contract between the renderer and <see cref="TerminalCursorController"/> that the
/// host's cursor handling rests on.
///
/// The host decorates frames from the PTY write hook rather than around every
/// <c>RenderOnceAsync</c> call, and that is only tolerable because the diff renderer hands the
/// PTY an EMPTY buffer when nothing changed. If that ever stopped holding, an idle screen would
/// re-issue the caret move at 30fps and the caret would stop blinking.
/// </summary>
public class FramePaintCursorHideTests
{
    /// <summary>Stands in for the host's LocalStdoutPty: same "decorate a non-empty frame" rule.</summary>
    private sealed class DecoratingPty : Andy.Tui.Backend.Terminal.IPtyIo
    {
        private readonly Func<string, string> _decorate;
        private readonly List<string> _sink;
        public DecoratingPty(Func<string, string> decorate, List<string> sink)
        {
            _decorate = decorate;
            _sink = sink;
        }

        public int PaintedFrames { get; private set; }
        public int EmptyFrames { get; private set; }

        public Task WriteAsync(ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken)
        {
            if (frameBytes.Length == 0)
            {
                EmptyFrames++;
                return Task.CompletedTask;
            }
            PaintedFrames++;
            _sink.Add(_decorate(System.Text.Encoding.UTF8.GetString(frameBytes.Span)));
            return Task.CompletedTask;
        }
    }

    private static DL.DisplayList Frame(string text)
    {
        var b = new DL.DisplayListBuilder();
        b.DrawText(new DL.TextRun(1, 1, text, new DL.Rgb24(200, 200, 200), null, DL.CellAttrFlags.None));
        return b.Build();
    }

    private sealed class Harness
    {
        public readonly List<string> Writes = new();
        public readonly TerminalCursorController Cursor;
        public readonly DecoratingPty Pty;
        public Andy.Tui.Core.FrameScheduler Scheduler = new(targetFps: 30);
        public readonly Andy.Tui.Backend.Terminal.TerminalCapabilities Caps =
            Andy.Tui.Backend.Terminal.CapabilityDetector.DetectFromEnvironment();
        public readonly (int Width, int Height) Viewport = (40, 10);

        public Harness()
        {
            Cursor = new TerminalCursorController(Writes.Add);
            Pty = new DecoratingPty(Cursor.DecorateFrame, Writes);
        }

        public async Task RenderAsync(string text)
        {
            await Scheduler.RenderOnceAsync(Frame(text), Viewport, Caps, Pty, CancellationToken.None);
            Cursor.AfterFrame();
        }
    }

    [Fact]
    public async Task AnUnchangedFrameNeverTouchesTheCursor()
    {
        var h = new Harness();
        h.Cursor.TargetCaret(3, 2);

        await h.RenderAsync("hello");
        h.Writes.Clear();

        // Idle: the same content re-rendered several times, as the 16ms loop does.
        for (int i = 0; i < 5; i++) await h.RenderAsync("hello");

        Assert.Equal(5, h.Pty.EmptyFrames);
        Assert.Empty(h.Writes);
        Assert.True(h.Cursor.IsVisible);
    }

    [Fact]
    public async Task AChangedFrameCarriesTheCaretBackToThePromptInOneSynchronizedWrite()
    {
        var h = new Harness();
        h.Cursor.TargetCaret(3, 2);

        await h.RenderAsync("hello");
        h.Writes.Clear();

        // Content changed - a live turn appending output - so the renderer repaints and would
        // otherwise walk the cursor across the changed cells.
        await h.RenderAsync("world");

        Assert.Equal(2, h.Pty.PaintedFrames);
        var write = Assert.Single(h.Writes);
        Assert.StartsWith(TerminalCursorController.BeginSyncSequence, write);
        Assert.EndsWith(
            "\u001b[2;3H" + TerminalCursorController.ShowSequence + TerminalCursorController.EndSyncSequence,
            write);
        Assert.True(h.Cursor.IsVisible);
    }
}
