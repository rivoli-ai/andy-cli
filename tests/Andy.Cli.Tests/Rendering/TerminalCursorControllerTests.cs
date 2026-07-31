using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Rendering;
using Xunit;

namespace Andy.Cli.Tests.Rendering;

/// <summary>
/// The caret must sit at the prompt and stay there: no jumping during a repaint, and no winking
/// off and on at frame rate either.
///
/// Andy.Tui repaints by walking the terminal cursor over every cell it touches and does not hide
/// it, so a cursor left visible during a frame appears to jump between the tool blocks, the feed
/// and the thinking footer before the host parks it back at the prompt caret. That regressed when
/// the "hide for the whole turn" guard was dropped so the composer could stay editable during an
/// active turn (#243). Hiding per paint fixed the jumping but made the caret itself flicker, so
/// the frame is now emitted as one synchronized update; these tests pin both halves.
/// </summary>
public class TerminalCursorControllerTests
{
    private static (TerminalCursorController Cursor, List<string> Writes) Create(bool visible = false)
    {
        var writes = new List<string>();
        return (new TerminalCursorController(s => writes.Add(s), visible), writes);
    }

    /// <summary>One painted frame, exactly as the PTY drives it.</summary>
    private static string Paint(TerminalCursorController cursor, string frame = "<frame>")
        => cursor.DecorateFrame(frame);

    [Fact]
    public void StartsHidden_MatchingTheAlternateScreenEntrySequence()
    {
        var (cursor, writes) = Create();

        Assert.False(cursor.IsVisible);
        Assert.Empty(writes);
    }

    [Fact]
    public void APaintedFrameIsWrappedInASynchronizedUpdate()
    {
        var (cursor, _) = Create();
        cursor.TargetCaret(7, 12);

        var output = Paint(cursor);

        // Balanced, and nothing outside the block: an unclosed BSU freezes the terminal's display.
        Assert.StartsWith(TerminalCursorController.BeginSyncSequence, output);
        Assert.EndsWith(TerminalCursorController.EndSyncSequence, output);
        Assert.Equal(1, CountOf(output, TerminalCursorController.BeginSyncSequence));
        Assert.Equal(1, CountOf(output, TerminalCursorController.EndSyncSequence));
    }

    [Fact]
    public void ThePaintAndTheCaretPlacementLandInOneWrite()
    {
        var (cursor, _) = Create();
        cursor.TargetCaret(7, 12);

        var output = Paint(cursor, "PAINT");

        // The move must follow the paint (which left the cursor elsewhere) and precede the end of
        // the synchronized block, so the terminal only ever presents the finished state.
        int paint = output.IndexOf("PAINT");
        int move = output.IndexOf("\u001b[12;7H");
        int show = output.IndexOf(TerminalCursorController.ShowSequence);
        int end = output.IndexOf(TerminalCursorController.EndSyncSequence);
        Assert.True(paint < move && move < show && show < end, Describe(output));
        Assert.True(cursor.IsVisible);
    }

    [Fact]
    public void AVisibleCaretIsHiddenBeforeThePaintForTerminalsWithoutSynchronizedOutput()
    {
        var (cursor, _) = Create();
        cursor.TargetCaret(3, 3);
        Paint(cursor);

        var output = Paint(cursor, "PAINT");

        // On a terminal that ignores DEC 2026 this is what stops the repaint dragging a visible
        // cursor across the screen.
        int hide = output.IndexOf(TerminalCursorController.HideSequence);
        Assert.True(hide >= 0 && hide < output.IndexOf("PAINT"), Describe(output));
    }

    [Fact]
    public void AnIdleFrameWritesNothingAtAll()
    {
        var (cursor, writes) = Create();
        cursor.TargetCaret(3, 2);
        Paint(cursor);
        cursor.AfterFrame();
        writes.Clear();

        // The 16ms loop keeps running with nothing to repaint. Re-issuing the move here would
        // restart the terminal's blink phase every frame and the caret would stop blinking.
        for (int i = 0; i < 5; i++)
        {
            cursor.AfterFrame();
        }

        Assert.Empty(writes);
        Assert.True(cursor.IsVisible);
    }

    [Fact]
    public void AnUnpaintedFrameStillMovesTheCaretWhenItChanged()
    {
        var (cursor, writes) = Create();
        cursor.TargetCaret(3, 2);
        Paint(cursor);
        cursor.AfterFrame();
        writes.Clear();

        // An arrow key on an otherwise unchanged screen: the display list is identical (the caret
        // is the terminal's own cursor, not a drawn glyph) so nothing repaints, but the caret has
        // to move anyway.
        cursor.TargetCaret(4, 2);
        cursor.AfterFrame();

        Assert.Equal(new[] { "\u001b[2;4H" }, writes);
    }

    [Fact]
    public void TheBlockCursorStyleIsAppliedOnce()
    {
        var (cursor, _) = Create();
        cursor.TargetCaret(1, 1);

        var output = string.Concat(Paint(cursor), Paint(cursor), Paint(cursor));

        Assert.Equal(1, CountOf(output, TerminalCursorController.StyleSequence));
    }

    [Fact]
    public void TheCaretIsNeverShownTwiceWithoutAHideInBetween()
    {
        var (cursor, _) = Create();
        cursor.TargetCaret(1, 1);

        var output = string.Concat(Paint(cursor), Paint(cursor), Paint(cursor));

        // Hide/show are paired per frame - never a stray show that a terminal without DEC 2026
        // would render as a flicker.
        Assert.Equal(
            CountOf(output, TerminalCursorController.HideSequence) + 1,
            CountOf(output, TerminalCursorController.ShowSequence));
    }

    [Fact]
    public void TargetHidden_KeepsTheCaretAwayWhileSomethingElseOwnsTheInputArea()
    {
        var (cursor, writes) = Create();
        cursor.TargetCaret(3, 3);
        Paint(cursor);
        writes.Clear();

        cursor.TargetHidden();
        var output = Paint(cursor);

        Assert.Contains(TerminalCursorController.HideSequence, output);
        Assert.DoesNotContain(TerminalCursorController.ShowSequence, output);
        Assert.False(cursor.IsVisible);

        // ...and it stays away on the idle frames that follow.
        writes.Clear();
        cursor.AfterFrame();
        cursor.AfterFrame();
        Assert.Empty(writes);
    }

    [Fact]
    public void TheCaretIsMovedBeforeItComesBackFromAModal()
    {
        var (cursor, _) = Create();
        cursor.TargetCaret(9, 4);
        Paint(cursor);

        // A modal owns the screen and paints with the caret hidden, which leaves the terminal's
        // cursor wherever that paint finished.
        cursor.TargetHidden();
        Paint(cursor, "modal");

        // Back to the composer, at the very same caret it had before the modal: showing without
        // moving would reveal it in the middle of where the modal was.
        cursor.TargetCaret(9, 4);
        var output = Paint(cursor, "restored");

        int move = output.IndexOf("[4;9H");
        int show = output.IndexOf(TerminalCursorController.ShowSequence);
        Assert.True(move >= 0 && move < show, Describe(output));
    }

    [Fact]
    public void Invalidate_ReAppliesStyleAndVisibilityAfterAnotherProgramOwnedTheTerminal()
    {
        var (cursor, _) = Create();
        cursor.TargetCaret(1, 1);
        Paint(cursor);

        // The external editor scribbled over the terminal and handed it back inside the
        // alternate screen with the cursor hidden and our shape gone.
        cursor.Invalidate();
        cursor.TargetCaret(6, 4);
        var output = Paint(cursor);

        Assert.Contains(TerminalCursorController.StyleSequence, output);
        Assert.Contains(TerminalCursorController.ShowSequence, output);
        Assert.DoesNotContain(TerminalCursorController.HideSequence, output);
    }

    [Fact]
    public void Invalidate_ReIssuesTheMoveEvenWhenTheCaretDidNotChange()
    {
        var (cursor, writes) = Create();
        cursor.TargetCaret(5, 5);
        Paint(cursor);
        cursor.AfterFrame();
        cursor.Invalidate();
        writes.Clear();

        // We no longer know where the terminal's cursor is, so the cached position must not
        // suppress the move.
        cursor.AfterFrame();

        Assert.Contains("\u001b[5;5H", string.Concat(writes));
    }

    [Fact]
    public void ALiveTurnNeverPresentsTheCaretAnywhereButTheCompactedPrompt()
    {
        var (cursor, _) = Create();
        cursor.TargetCaret(8, 20);

        // Three frames of a turn appending output. Each frame's bytes are what the terminal sees.
        foreach (var frame in new[] { "tool output", "more output", "thinking footer" })
        {
            var output = Paint(cursor, frame);
            cursor.AfterFrame();

            // Within the block: the paint comes first, then the move to the prompt, and the caret
            // is only made visible once it is there (the style rides along on the first frame).
            int body = output.IndexOf(frame);
            int move = output.IndexOf("\u001b[20;8H");
            Assert.True(body >= 0 && body < move, Describe(output));
            Assert.EndsWith(
                TerminalCursorController.ShowSequence + TerminalCursorController.EndSyncSequence,
                output);
        }

        Assert.True(cursor.IsVisible);
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0;
        for (int i = 0; i <= haystack.Length - needle.Length;)
        {
            int hit = haystack.IndexOf(needle, i);
            if (hit < 0) break;
            count++;
            i = hit + needle.Length;
        }
        return count;
    }

    private static string Describe(string output) => "output: " + output.Replace("\u001b", "ESC");
}
