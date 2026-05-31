using System;
using System.Collections.Generic;
using System.Text;

namespace Andy.Cli.Input;

/// <summary>The kind of event produced by <see cref="TerminalInputParser"/>.</summary>
public enum TerminalInputKind
{
    /// <summary>A keyboard event; see <see cref="TerminalInputEvent.Key"/>.</summary>
    Key,
    /// <summary>A mouse wheel scroll; see <see cref="TerminalInputEvent.WheelDelta"/>.</summary>
    Wheel
}

/// <summary>
/// A single decoded terminal input event. Keys are surfaced as <see cref="ConsoleKeyInfo"/>
/// so they flow through the same handlers used by the legacy <c>Console.ReadKey</c> path.
/// </summary>
public readonly struct TerminalInputEvent
{
    public TerminalInputKind Kind { get; }
    /// <summary>Valid when <see cref="Kind"/> is <see cref="TerminalInputKind.Key"/>.</summary>
    public ConsoleKeyInfo Key { get; }
    /// <summary>Wheel notches: positive scrolls up (toward older content), negative scrolls down.</summary>
    public int WheelDelta { get; }

    private TerminalInputEvent(TerminalInputKind kind, ConsoleKeyInfo key, int wheelDelta)
    {
        Kind = kind;
        Key = key;
        WheelDelta = wheelDelta;
    }

    public static TerminalInputEvent FromKey(ConsoleKeyInfo key) => new(TerminalInputKind.Key, key, 0);
    public static TerminalInputEvent FromWheel(int delta) => new(TerminalInputKind.Wheel, default, delta);
}

/// <summary>
/// A small, stateful VT/ANSI input decoder. It turns raw stdin bytes into
/// <see cref="TerminalInputEvent"/>s, covering every key the CLI relies on
/// (arrows, Home/End, Delete, PageUp/PageDown, Backspace, Enter, Tab, Escape,
/// Ctrl+letter, Ctrl+], printable text) plus SGR mouse-wheel events.
///
/// We parse keys ourselves rather than leaning on Andy.Tui.Input's decoder
/// because that decoder drops Backspace, Escape, Delete, Home/End and
/// PageUp/PageDown — all of which the CLI needs. The decoder remains the
/// reference for SGR mouse wheel, whose encoding we mirror here.
///
/// Partial sequences are buffered between <see cref="Feed"/> calls. A lone
/// ESC byte is held pending until <see cref="Flush"/> is called (after an idle
/// tick) so it can be disambiguated from the start of an escape sequence.
/// </summary>
public sealed class TerminalInputParser
{
    private readonly List<byte> _buf = new();

    /// <summary>Feed raw bytes and return any complete events decoded so far.</summary>
    public IReadOnlyList<TerminalInputEvent> Feed(byte[] data, int length)
    {
        var outEvents = new List<TerminalInputEvent>();
        for (int i = 0; i < length; i++) _buf.Add(data[i]);
        Drain(outEvents);
        return outEvents;
    }

    /// <summary>Convenience overload feeding the whole array.</summary>
    public IReadOnlyList<TerminalInputEvent> Feed(byte[] data) => Feed(data, data.Length);

    /// <summary>
    /// Resolve anything left pending. Currently this only matters for a lone
    /// ESC byte, which becomes <see cref="ConsoleKey.Escape"/> once we know no
    /// continuation bytes are arriving.
    /// </summary>
    public IReadOnlyList<TerminalInputEvent> Flush()
    {
        var outEvents = new List<TerminalInputEvent>();
        if (_buf.Count == 1 && _buf[0] == 0x1B)
        {
            _buf.Clear();
            outEvents.Add(Esc());
        }
        return outEvents;
    }

    private void Drain(List<TerminalInputEvent> outEvents)
    {
        // Consume as many complete tokens from the front of the buffer as we
        // can. Stop when the head is an incomplete sequence (need more bytes).
        while (_buf.Count > 0)
        {
            int consumed = TryConsume(outEvents);
            if (consumed <= 0) break; // incomplete; wait for more bytes
            _buf.RemoveRange(0, consumed);
        }
    }

    /// <summary>
    /// Try to decode one token at the front of the buffer. Returns the number
    /// of bytes consumed, or 0 if the buffer holds an incomplete sequence.
    /// </summary>
    private int TryConsume(List<TerminalInputEvent> outEvents)
    {
        byte b = _buf[0];

        if (b == 0x1B) return TryConsumeEscape(outEvents);

        // Single-byte controls and printable bytes.
        switch (b)
        {
            case 0x0D: outEvents.Add(Key('\r', ConsoleKey.Enter)); return 1;          // CR -> Enter (submit)
            case 0x0A: outEvents.Add(Key('\n', ConsoleKey.Enter, control: true)); return 1; // LF -> Ctrl+Enter (newline)
            case 0x09: outEvents.Add(Key('\t', ConsoleKey.Tab)); return 1;
            case 0x7F: outEvents.Add(Key('\b', ConsoleKey.Backspace)); return 1;       // DEL -> Backspace
            case 0x08: outEvents.Add(Key('\b', ConsoleKey.Backspace)); return 1;       // BS  -> Backspace
            case 0x1D: outEvents.Add(Key('\u001d', ConsoleKey.Oem6, control: true)); return 1; // Ctrl+]
            case 0x00: return 1; // ignore NUL
        }

        if (b >= 0x01 && b <= 0x1A)
        {
            // Ctrl+A .. Ctrl+Z (0x08/0x09/0x0A/0x0D handled above).
            var key = (ConsoleKey)('A' + (b - 1));
            outEvents.Add(Key((char)b, key, control: true));
            return 1;
        }

        if (b >= 0x20 && b <= 0x7E)
        {
            outEvents.Add(Printable((char)b));
            return 1;
        }

        if (b >= 0x80)
            return TryConsumeUtf8(outEvents);

        // Remaining C0 controls we don't map: drop the byte.
        return 1;
    }

    private int TryConsumeEscape(List<TerminalInputEvent> outEvents)
    {
        if (_buf.Count < 2) return 0; // could be lone ESC or start of a sequence

        byte second = _buf[1];
        if (second == (byte)'[') return TryConsumeCsi(outEvents);
        if (second == (byte)'O') return TryConsumeSs3(outEvents);

        // ESC followed by something else: treat ESC as a standalone Escape and
        // let the following byte be parsed on its own. (Alt+key chords are not
        // used by the CLI.)
        outEvents.Add(Esc());
        return 1;
    }

    // CSI: ESC '[' ... final-byte(0x40-0x7E)
    private int TryConsumeCsi(List<TerminalInputEvent> outEvents)
    {
        // SGR mouse: ESC '[' '<' params ('M' | 'm')
        if (_buf.Count >= 3 && _buf[2] == (byte)'<')
            return TryConsumeSgrMouse(outEvents);

        int i = 2;
        while (i < _buf.Count)
        {
            byte c = _buf[i];
            if (c >= 0x40 && c <= 0x7E) // final byte
            {
                string paramStr = AsciiSlice(2, i);
                DecodeCsi(paramStr, (char)c, outEvents);
                return i + 1;
            }
            i++;
        }
        return 0; // no final byte yet
    }

    private int TryConsumeSgrMouse(List<TerminalInputEvent> outEvents)
    {
        int i = 3;
        while (i < _buf.Count)
        {
            byte c = _buf[i];
            if (c == (byte)'M' || c == (byte)'m')
            {
                string paramStr = AsciiSlice(3, i); // "btn;col;row"
                var parts = paramStr.Split(';');
                if (parts.Length >= 1 && int.TryParse(parts[0], out int btn))
                {
                    // Wheel buttons have bit 0x40 set; low 2 bits select direction.
                    if ((btn & 0x40) != 0)
                    {
                        int low = btn & 0x03;
                        if (low == 0) outEvents.Add(TerminalInputEvent.FromWheel(+1));      // wheel up
                        else if (low == 1) outEvents.Add(TerminalInputEvent.FromWheel(-1)); // wheel down
                        // low 2/3 are horizontal wheel; ignored.
                    }
                    // Non-wheel mouse (press/release/move) has no CLI behavior.
                }
                return i + 1;
            }
            i++;
        }
        return 0; // sequence not terminated yet
    }

    // SS3: ESC 'O' final
    private int TryConsumeSs3(List<TerminalInputEvent> outEvents)
    {
        if (_buf.Count < 3) return 0;
        char f = (char)_buf[2];
        switch (f)
        {
            case 'A': outEvents.Add(Key('\0', ConsoleKey.UpArrow)); return 3;
            case 'B': outEvents.Add(Key('\0', ConsoleKey.DownArrow)); return 3;
            case 'C': outEvents.Add(Key('\0', ConsoleKey.RightArrow)); return 3;
            case 'D': outEvents.Add(Key('\0', ConsoleKey.LeftArrow)); return 3;
            case 'H': outEvents.Add(Key('\0', ConsoleKey.Home)); return 3;
            case 'F': outEvents.Add(Key('\0', ConsoleKey.End)); return 3;
            case 'P': outEvents.Add(Key('\0', ConsoleKey.F1)); return 3;
            case 'Q': outEvents.Add(Key('\0', ConsoleKey.F2)); return 3;
            case 'R': outEvents.Add(Key('\0', ConsoleKey.F3)); return 3;
            case 'S': outEvents.Add(Key('\0', ConsoleKey.F4)); return 3;
            default: return 3; // unknown SS3; drop
        }
    }

    private void DecodeCsi(string paramStr, char final, List<TerminalInputEvent> outEvents)
    {
        // paramStr looks like "", "5", "1;5" etc. The second numeric parameter,
        // when present, is the xterm modifier code (1 + bitmask).
        int firstParam = 0;
        var mods = ConsoleModifiers.None;
        if (paramStr.Length > 0)
        {
            var parts = paramStr.Split(';');
            int.TryParse(parts[0], out firstParam);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int modCode) && modCode > 1)
                mods = DecodeModifier(modCode);
        }

        switch (final)
        {
            case 'A': outEvents.Add(Key('\0', ConsoleKey.UpArrow, mods)); return;
            case 'B': outEvents.Add(Key('\0', ConsoleKey.DownArrow, mods)); return;
            case 'C': outEvents.Add(Key('\0', ConsoleKey.RightArrow, mods)); return;
            case 'D': outEvents.Add(Key('\0', ConsoleKey.LeftArrow, mods)); return;
            case 'H': outEvents.Add(Key('\0', ConsoleKey.Home, mods)); return;
            case 'F': outEvents.Add(Key('\0', ConsoleKey.End, mods)); return;
            case 'Z': outEvents.Add(Key('\t', ConsoleKey.Tab, ConsoleModifiers.Shift)); return; // Shift+Tab
            case '~':
                switch (firstParam)
                {
                    case 1:
                    case 7: outEvents.Add(Key('\0', ConsoleKey.Home, mods)); return;
                    case 2: outEvents.Add(Key('\0', ConsoleKey.Insert, mods)); return;
                    case 3: outEvents.Add(Key('\0', ConsoleKey.Delete, mods)); return;
                    case 4:
                    case 8: outEvents.Add(Key('\0', ConsoleKey.End, mods)); return;
                    case 5: outEvents.Add(Key('\0', ConsoleKey.PageUp, mods)); return;
                    case 6: outEvents.Add(Key('\0', ConsoleKey.PageDown, mods)); return;
                    case 11: outEvents.Add(Key('\0', ConsoleKey.F1, mods)); return;
                    case 12: outEvents.Add(Key('\0', ConsoleKey.F2, mods)); return;
                    case 13: outEvents.Add(Key('\0', ConsoleKey.F3, mods)); return;
                    case 14: outEvents.Add(Key('\0', ConsoleKey.F4, mods)); return;
                    case 15: outEvents.Add(Key('\0', ConsoleKey.F5, mods)); return;
                    default: return; // unknown ~ sequence
                }
            default:
                return; // unknown CSI; drop
        }
    }

    private static ConsoleModifiers DecodeModifier(int modCode)
    {
        // xterm: modCode = 1 + (1*shift + 2*alt + 4*ctrl + 8*meta)
        int bits = modCode - 1;
        var m = ConsoleModifiers.None;
        if ((bits & 1) != 0) m |= ConsoleModifiers.Shift;
        if ((bits & 2) != 0) m |= ConsoleModifiers.Alt;
        if ((bits & 4) != 0) m |= ConsoleModifiers.Control;
        return m;
    }

    private int TryConsumeUtf8(List<TerminalInputEvent> outEvents)
    {
        byte lead = _buf[0];
        int len = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
        if (len == 1)
        {
            // Stray continuation/invalid byte; drop it.
            return 1;
        }
        if (_buf.Count < len) return 0; // incomplete multibyte sequence

        var bytes = new byte[len];
        for (int i = 0; i < len; i++) bytes[i] = _buf[i];
        string s;
        try { s = Encoding.UTF8.GetString(bytes); }
        catch { return len; }
        foreach (char ch in s)
            outEvents.Add(Printable(ch));
        return len;
    }

    private string AsciiSlice(int start, int endExclusive)
    {
        var sb = new StringBuilder(endExclusive - start);
        for (int i = start; i < endExclusive; i++) sb.Append((char)_buf[i]);
        return sb.ToString();
    }

    private static TerminalInputEvent Printable(char c)
    {
        ConsoleKey key = 0;
        bool shift = false;
        if (c >= 'a' && c <= 'z') key = (ConsoleKey)('A' + (c - 'a'));
        else if (c >= 'A' && c <= 'Z') { key = (ConsoleKey)c; shift = true; }
        else if (c >= '0' && c <= '9') key = ConsoleKey.D0 + (c - '0');
        else if (c == ' ') key = ConsoleKey.Spacebar;
        return TerminalInputEvent.FromKey(new ConsoleKeyInfo(c, key, shift, false, false));
    }

    private static TerminalInputEvent Key(char ch, ConsoleKey key, bool control = false)
        => TerminalInputEvent.FromKey(new ConsoleKeyInfo(ch, key, false, false, control));

    private static TerminalInputEvent Key(char ch, ConsoleKey key, ConsoleModifiers mods)
        => TerminalInputEvent.FromKey(new ConsoleKeyInfo(
            ch, key,
            (mods & ConsoleModifiers.Shift) != 0,
            (mods & ConsoleModifiers.Alt) != 0,
            (mods & ConsoleModifiers.Control) != 0));

    private static TerminalInputEvent Esc() => Key('\u001b', ConsoleKey.Escape);
}
