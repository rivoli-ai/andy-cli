using System;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Covers the editing primitive the @file completion uses to swap a mention token for a full path.
/// </summary>
public class PromptLineReplaceRangeTests
{
    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static PromptLine PromptWith(string text)
    {
        var prompt = new PromptLine();
        prompt.SetText(text);
        return prompt;
    }

    [Fact]
    public void ReplaceRange_ReplacesTheRequestedSpanAndMovesTheCaret()
    {
        var prompt = PromptWith("hello world");

        prompt.ReplaceRange(6, 5, "there");

        Assert.Equal("hello there", prompt.Text);
        Assert.Equal(11, prompt.CursorPosition);
    }

    [Fact]
    public void ReplaceRange_WithExplicitCursor_HonoursIt()
    {
        var prompt = PromptWith("hello world");

        prompt.ReplaceRange(0, 5, "hi", newCursor: 0);

        Assert.Equal("hi world", prompt.Text);
        Assert.Equal(0, prompt.CursorPosition);
    }

    [Fact]
    public void ReplaceRange_InsertsWhenLengthIsZero()
    {
        var prompt = PromptWith("ab");

        prompt.ReplaceRange(1, 0, "XY");

        Assert.Equal("aXYb", prompt.Text);
        Assert.Equal(3, prompt.CursorPosition);
    }

    [Fact]
    public void ReplaceRange_ClampsOutOfRangeArguments()
    {
        var prompt = PromptWith("abc");

        prompt.ReplaceRange(-5, 100, "z", newCursor: 999);

        Assert.Equal("z", prompt.Text);
        Assert.Equal(1, prompt.CursorPosition);
    }

    [Fact]
    public void ReplaceRange_OnAnInnerLine_LeavesOtherLinesUntouched()
    {
        var prompt = PromptWith("one\ntwo\nthree");

        prompt.ReplaceRange(4, 3, "TWO");
        prompt.OnKey(Char('!'));

        Assert.Equal("one\nTWO!\nthree", prompt.Text);
    }

    [Fact]
    public void CursorPosition_TracksOrdinaryEditing()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('a'));
        prompt.OnKey(Char('b'));
        Assert.Equal(2, prompt.CursorPosition);

        prompt.OnKey(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false));
        Assert.Equal(1, prompt.CursorPosition);

        prompt.OnKey(new ConsoleKeyInfo('\0', ConsoleKey.Backspace, false, false, false));
        Assert.Equal(0, prompt.CursorPosition);
        Assert.Equal("b", prompt.Text);
    }
}
