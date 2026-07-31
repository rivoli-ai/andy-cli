using System;
using System.Text;

namespace Andy.Cli.Rendering;

/// <summary>
/// Owns the hardware terminal cursor: its visibility, its shape and where it is parked
/// between frames.
///
/// <para>The renderer repaints by walking the cursor over every cell it touches, and it never
/// hides the cursor while doing so. A visible cursor therefore appears to jump around the
/// screen - over tool blocks, the feed and the thinking footer - before the host moves it back
/// to the prompt caret at the end of the frame. That used to be masked by hiding the cursor for
/// the whole of an active turn; the composer stays editable during a turn now (#243), so the
/// caret has to remain visible and the flicker has to be solved properly instead.</para>
///
/// <para>The frame is therefore wrapped in synchronized output (DEC mode 2026): a conforming
/// terminal buffers everything between BSU and ESU and presents it as one image, so the hide,
/// the repaint and the move to the caret are never seen as separate states. The cursor simply
/// stays where it belongs. Terminals without 2026 ignore the mode and fall back to the hide -
/// still no jumping, just a caret that winks during a repaint.</para>
///
/// <para>Everything for one frame is produced by a single <see cref="DecorateFrame"/> call and
/// written in one go, so a synchronized block can never be left open - an unbalanced BSU would
/// freeze the terminal's display.</para>
/// </summary>
public sealed class TerminalCursorController
{
    /// <summary>DECTCEM off - hide the cursor.</summary>
    public const string HideSequence = "\u001b[?25l";

    /// <summary>DECTCEM on - show the cursor.</summary>
    public const string ShowSequence = "\u001b[?25h";

    /// <summary>DECSCUSR 1 - blinking block. Emitted once per terminal ownership.</summary>
    public const string StyleSequence = "\u001b[1 q";

    /// <summary>Begin Synchronized Update. Ignored by terminals that do not implement DEC 2026.</summary>
    public const string BeginSyncSequence = "\u001b[?2026h";

    /// <summary>End Synchronized Update. Must always follow a <see cref="BeginSyncSequence"/>.</summary>
    public const string EndSyncSequence = "\u001b[?2026l";

    private readonly Action<string> _write;
    private bool _styleApplied;
    private bool _visible;
    private bool _caretWanted;
    private int _wantCol, _wantRow;
    private int _atCol, _atRow;
    private bool _painted;

    /// <param name="write">Terminal sink. Must be the same sink the renderer writes frames to,
    /// otherwise cursor control can be reordered against the paint it is wrapped around.</param>
    /// <param name="visible">Whether the cursor is currently visible. The host enters the
    /// alternate screen with it hidden, which is the default.</param>
    public TerminalCursorController(Action<string> write, bool visible = false)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _visible = visible;
    }

    /// <summary>True while the cursor is shown, as far as this controller knows.</summary>
    public bool IsVisible => _visible;

    /// <summary>
    /// Where the caret belongs (1-based) once the frame being built lands. Set before rendering;
    /// it holds until changed, so the modal render loops inherit whatever the host last asked for.
    /// </summary>
    public void TargetCaret(int col1, int row1)
    {
        _caretWanted = true;
        _wantCol = col1;
        _wantRow = row1;
    }

    /// <summary>Keep the cursor away: something other than the composer owns the input area.</summary>
    public void TargetHidden() => _caretWanted = false;

    /// <summary>
    /// Wrap one painted frame. The returned string is the complete byte stream for that frame -
    /// synchronized-update begin, the hide, the frame itself, the caret placement, synchronized-update
    /// end - and must be written as-is. Only call this for a frame that actually paints something.
    /// </summary>
    public string DecorateFrame(string frame)
    {
        var sb = new StringBuilder((frame?.Length ?? 0) + 48);
        sb.Append(BeginSyncSequence);
        if (_visible)
        {
            // Belt and braces for terminals without DEC 2026: without this the repaint below
            // drags a visible cursor across every cell it touches.
            sb.Append(HideSequence);
            _visible = false;
        }
        sb.Append(frame);
        // The paint left the cursor on whatever cell it finished with, so where we last put it
        // means nothing now. Zeroing this both forces the move below and stops a later show from
        // revealing the caret at a stale position - which is what a modal, painting with the caret
        // hidden, would otherwise leave behind.
        _atCol = _atRow = 0;
        AppendPlacement(sb);
        sb.Append(EndSyncSequence);
        _painted = true;
        return sb.ToString();
    }

    /// <summary>
    /// Called after each render. When the frame painted, the caret was already placed inside the
    /// synchronized block and this does nothing. When it did not paint - the diff renderer had no
    /// work - this emits the placement on its own, which is what makes an arrow key move the caret
    /// on an otherwise unchanged screen.
    /// </summary>
    public void AfterFrame()
    {
        if (_painted)
        {
            _painted = false;
            return;
        }

        var sb = new StringBuilder();
        AppendPlacement(sb);
        if (sb.Length > 0) SafeWrite(sb.ToString());
    }

    /// <summary>
    /// Forget everything we believed about the terminal's cursor state. Call after another
    /// program has owned the terminal (the external editor), which hands it back inside the
    /// alternate screen with the cursor hidden and our shape gone; the next frame re-applies both.
    /// </summary>
    public void Invalidate()
    {
        _styleApplied = false;
        _visible = false;
        _atCol = _atRow = 0;
    }

    private void AppendPlacement(StringBuilder sb)
    {
        if (!_caretWanted)
        {
            if (_visible)
            {
                sb.Append(HideSequence);
                _visible = false;
            }
            return;
        }

        // Skipping the redundant move matters: on an idle screen this method runs every frame,
        // and terminals restart the blink phase when the cursor moves. A paint always zeroes the
        // tracked position first, so "redundant" only ever means "nothing has touched it".
        if (_atCol != _wantCol || _atRow != _wantRow)
        {
            sb.Append("\u001b[").Append(_wantRow).Append(';').Append(_wantCol).Append('H');
            _atCol = _wantCol;
            _atRow = _wantRow;
        }
        if (!_styleApplied)
        {
            sb.Append(StyleSequence);
            _styleApplied = true;
        }
        if (!_visible)
        {
            sb.Append(ShowSequence);
            _visible = true;
        }
    }

    private void SafeWrite(string s)
    {
        try { _write(s); } catch { /* terminal may be gone */ }
    }
}
