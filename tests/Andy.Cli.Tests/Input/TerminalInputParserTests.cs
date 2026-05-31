using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Andy.Cli.Input;
using Xunit;

namespace Andy.Cli.Tests.Input;

/// <summary>
/// Tests for <see cref="TerminalInputParser"/>, the raw-stdin VT decoder that
/// replaced the Console.ReadKey path so the CLI can receive mouse wheel events.
/// It must keep decoding every key the CLI relies on (the bundled
/// Andy.Tui.Input decoder drops Backspace, Escape, Delete, Home/End and
/// PageUp/PageDown, which is why we parse keys ourselves).
/// </summary>
public class TerminalInputParserTests
{
    private static List<TerminalInputEvent> Decode(params byte[] bytes)
        => new TerminalInputParser().Feed(bytes).ToList();

    private static List<TerminalInputEvent> Decode(string ascii)
        => new TerminalInputParser().Feed(Encoding.ASCII.GetBytes(ascii)).ToList();

    private static TerminalInputEvent Single(IReadOnlyList<TerminalInputEvent> evs)
    {
        Assert.Single(evs);
        return evs[0];
    }

    // ----- Mouse wheel (the headline feature) -----

    [Fact]
    public void SgrWheelUp_ProducesPositiveWheelDelta()
    {
        var ev = Single(Decode("\x1b[<64;10;5M"));
        Assert.Equal(TerminalInputKind.Wheel, ev.Kind);
        Assert.True(ev.WheelDelta > 0, "wheel up should scroll toward older content (positive)");
    }

    [Fact]
    public void SgrWheelDown_ProducesNegativeWheelDelta()
    {
        var ev = Single(Decode("\x1b[<65;10;5M"));
        Assert.Equal(TerminalInputKind.Wheel, ev.Kind);
        Assert.True(ev.WheelDelta < 0, "wheel down should scroll toward newer content (negative)");
    }

    [Fact]
    public void SgrWheelWithModifiers_StillDecodesDirection()
    {
        // Ctrl held while wheeling sets extra bits (0x10); direction bits remain.
        var up = Single(Decode("\x1b[<80;1;1M"));   // 64 | 16
        Assert.Equal(TerminalInputKind.Wheel, up.Kind);
        Assert.True(up.WheelDelta > 0);
    }

    [Fact]
    public void NonWheelMouseEvents_AreIgnored()
    {
        // Left button press (button 0) carries no CLI behavior.
        Assert.Empty(Decode("\x1b[<0;10;5M"));
        // Release.
        Assert.Empty(Decode("\x1b[<0;10;5m"));
    }

    // ----- Keys the Andy.Tui.Input decoder drops -----

    [Fact]
    public void PageUp_DecodesToPageUpKey()
    {
        var ev = Single(Decode("\x1b[5~"));
        Assert.Equal(TerminalInputKind.Key, ev.Kind);
        Assert.Equal(ConsoleKey.PageUp, ev.Key.Key);
    }

    [Fact]
    public void PageDown_DecodesToPageDownKey()
    {
        var ev = Single(Decode("\x1b[6~"));
        Assert.Equal(ConsoleKey.PageDown, ev.Key.Key);
    }

    [Fact]
    public void Backspace_Del7f_DecodesToBackspace()
    {
        var ev = Single(Decode(0x7f));
        Assert.Equal(ConsoleKey.Backspace, ev.Key.Key);
    }

    [Fact]
    public void Delete_DecodesToDeleteKey()
    {
        var ev = Single(Decode("\x1b[3~"));
        Assert.Equal(ConsoleKey.Delete, ev.Key.Key);
    }

    [Fact]
    public void HomeAndEnd_Decode()
    {
        Assert.Equal(ConsoleKey.Home, Single(Decode("\x1b[H")).Key.Key);
        Assert.Equal(ConsoleKey.End, Single(Decode("\x1b[F")).Key.Key);
    }

    [Fact]
    public void LoneEscape_RequiresFlush_ThenDecodesEscape()
    {
        var parser = new TerminalInputParser();
        // A single ESC byte is held pending (could begin a sequence).
        Assert.Empty(parser.Feed(new byte[] { 0x1b }));
        // The idle-tick flush resolves it to Escape.
        var flushed = parser.Flush();
        var ev = Single(flushed);
        Assert.Equal(ConsoleKey.Escape, ev.Key.Key);
    }

    [Fact]
    public void CtrlRightBracket_DecodesToOem6WithControl()
    {
        var ev = Single(Decode(0x1d));
        Assert.Equal(ConsoleKey.Oem6, ev.Key.Key);
        Assert.True((ev.Key.Modifiers & ConsoleModifiers.Control) != 0);
        Assert.Equal('\u001d', ev.Key.KeyChar);
    }

    // ----- Standard keys -----

    [Fact]
    public void Arrows_Decode()
    {
        Assert.Equal(ConsoleKey.UpArrow, Single(Decode("\x1b[A")).Key.Key);
        Assert.Equal(ConsoleKey.DownArrow, Single(Decode("\x1b[B")).Key.Key);
        Assert.Equal(ConsoleKey.RightArrow, Single(Decode("\x1b[C")).Key.Key);
        Assert.Equal(ConsoleKey.LeftArrow, Single(Decode("\x1b[D")).Key.Key);
    }

    [Fact]
    public void Ss3Arrows_Decode()
    {
        Assert.Equal(ConsoleKey.UpArrow, Single(Decode("\x1bOA")).Key.Key);
        Assert.Equal(ConsoleKey.F2, Single(Decode("\x1bOQ")).Key.Key);
    }

    [Fact]
    public void CtrlUpArrow_CarriesControlModifier()
    {
        var ev = Single(Decode("\x1b[1;5A"));
        Assert.Equal(ConsoleKey.UpArrow, ev.Key.Key);
        Assert.True((ev.Key.Modifiers & ConsoleModifiers.Control) != 0);
    }

    [Fact]
    public void EnterCr_SubmitsAsPlainEnter()
    {
        var ev = Single(Decode(0x0d));
        Assert.Equal(ConsoleKey.Enter, ev.Key.Key);
        Assert.Equal(ConsoleModifiers.None, ev.Key.Modifiers & ConsoleModifiers.Control);
    }

    [Fact]
    public void EnterLf_MapsToCtrlEnterForNewline()
    {
        var ev = Single(Decode(0x0a));
        Assert.Equal(ConsoleKey.Enter, ev.Key.Key);
        Assert.True((ev.Key.Modifiers & ConsoleModifiers.Control) != 0);
    }

    [Fact]
    public void CtrlLetters_DecodeWithControlModifier()
    {
        var d = Single(Decode(0x04)); // Ctrl+D
        Assert.Equal(ConsoleKey.D, d.Key.Key);
        Assert.True((d.Key.Modifiers & ConsoleModifiers.Control) != 0);

        var p = Single(Decode(0x10)); // Ctrl+P
        Assert.Equal(ConsoleKey.P, p.Key.Key);
        Assert.True((p.Key.Modifiers & ConsoleModifiers.Control) != 0);
    }

    [Fact]
    public void PrintableLetters_CarryKeyChar()
    {
        var lower = Single(Decode("a"));
        Assert.Equal('a', lower.Key.KeyChar);
        Assert.Equal(ConsoleKey.A, lower.Key.Key);
        Assert.False((lower.Key.Modifiers & ConsoleModifiers.Shift) != 0);

        var upper = Single(Decode("A"));
        Assert.Equal('A', upper.Key.KeyChar);
        Assert.True((upper.Key.Modifiers & ConsoleModifiers.Shift) != 0);
    }

    [Fact]
    public void PrintableSequence_DecodesEachCharInOrder()
    {
        var evs = Decode("ab/1");
        Assert.Equal(4, evs.Count);
        Assert.Equal('a', evs[0].Key.KeyChar);
        Assert.Equal('b', evs[1].Key.KeyChar);
        Assert.Equal('/', evs[2].Key.KeyChar);
        Assert.Equal('1', evs[3].Key.KeyChar);
        Assert.Equal(ConsoleKey.D1, evs[3].Key.Key);
    }

    // ----- Stream behavior -----

    [Fact]
    public void SplitEscapeSequence_AcrossFeeds_DecodesOnce()
    {
        var parser = new TerminalInputParser();
        Assert.Empty(parser.Feed(new byte[] { 0x1b })); // pending
        Assert.Empty(parser.Feed(new byte[] { (byte)'[' })); // still pending
        var evs = parser.Feed(new byte[] { (byte)'A' });
        var ev = Single(evs);
        Assert.Equal(ConsoleKey.UpArrow, ev.Key.Key);
    }

    [Fact]
    public void MixedKeyAndWheelBurst_DecodesBoth()
    {
        // 'h', wheel-up, 'i' arriving in one read.
        var evs = new TerminalInputParser().Feed(Encoding.ASCII.GetBytes("h\x1b[<64;1;1Mi"));
        Assert.Equal(3, evs.Count);
        Assert.Equal('h', evs[0].Key.KeyChar);
        Assert.Equal(TerminalInputKind.Wheel, evs[1].Kind);
        Assert.Equal('i', evs[2].Key.KeyChar);
    }

    [Fact]
    public void Utf8MultibyteChar_DecodesToSingleKey()
    {
        // 'é' is 0xC3 0xA9 in UTF-8.
        var ev = Single(new TerminalInputParser().Feed(new byte[] { 0xC3, 0xA9 }));
        Assert.Equal(TerminalInputKind.Key, ev.Kind);
        Assert.Equal('é', ev.Key.KeyChar);
    }
}
