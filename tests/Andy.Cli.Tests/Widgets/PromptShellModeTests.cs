using System;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Composer mode transitions for shell escape (issue #286): entering with "!" at prompt offset
/// zero, leaving with Escape or Backspace on an empty shell prompt, and the guards that stop the
/// mode from being armed by accident.
/// </summary>
public class PromptShellModeTests
{
    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
    private static ConsoleKeyInfo Enter() => new('\r', ConsoleKey.Enter, false, false, false);

    private static void Type(PromptLine prompt, string text)
    {
        foreach (var c in text)
        {
            prompt.OnKey(Char(c));
            // Defeat the paste heuristic, which treats keys arriving within 30ms as a paste.
            System.Threading.Thread.Sleep(35);
        }
    }

    [Fact]
    public void Bang_AtOffsetZeroOnEmptyPrompt_EntersShellMode()
    {
        var prompt = new PromptLine();

        var submitted = prompt.OnKey(Char('!'));

        Assert.Null(submitted);
        Assert.Equal(PromptMode.Shell, prompt.Mode);
        // The trigger character is consumed by the mode switch, not inserted.
        Assert.Equal(string.Empty, prompt.Text);
    }

    [Fact]
    public void Bang_AfterOtherText_IsAnOrdinaryCharacter()
    {
        var prompt = new PromptLine();
        Type(prompt, "wow!");

        Assert.Equal(PromptMode.Normal, prompt.Mode);
        Assert.Equal("wow!", prompt.Text);
    }

    [Fact]
    public void Bang_WhileAlreadyInShellMode_IsAnOrdinaryCharacter()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));
        System.Threading.Thread.Sleep(35);

        prompt.OnKey(Char('!'));

        Assert.Equal(PromptMode.Shell, prompt.Mode);
        Assert.Equal("!", prompt.Text);
    }

    [Fact]
    public void Bang_DuringPaste_DoesNotEnterShellMode()
    {
        var prompt = new PromptLine();

        // Two keys within the paste-detection window mark the burst as a paste; the second key is
        // the "!" we must not treat as a mode switch. Start with a character so the prompt is not
        // empty on the burst's first key either.
        prompt.OnKey(Char('a'));
        prompt.OnKey(Key(ConsoleKey.Backspace)); // back to empty, still inside the paste window
        prompt.OnKey(Char('!'));

        Assert.Equal(PromptMode.Normal, prompt.Mode);
        Assert.Equal("!", prompt.Text);
    }

    [Fact]
    public void InsertText_StartingWithBang_DoesNotEnterShellMode()
    {
        // Bracketed paste goes through InsertText, which never inspects the first character.
        var prompt = new PromptLine();

        prompt.InsertText("!/bin/bash\necho hi\n");

        Assert.Equal(PromptMode.Normal, prompt.Mode);
        Assert.StartsWith("!", prompt.Text);
    }

    [Fact]
    public void Escape_OnEmptyShellPrompt_ReturnsToNormalMode()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));

        Assert.True(prompt.TryExitShellMode());
        Assert.Equal(PromptMode.Normal, prompt.Mode);
    }

    [Fact]
    public void Escape_WithTextOnTheShellPrompt_StaysInShellMode()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));
        System.Threading.Thread.Sleep(35);
        Type(prompt, "ls");

        // False here is what tells the interactive loop to fall through to the exit dialog, so
        // Escape never silently stops meaning "quit".
        Assert.False(prompt.TryExitShellMode());
        Assert.Equal(PromptMode.Shell, prompt.Mode);
    }

    [Fact]
    public void EscapeKey_OnEmptyShellPrompt_ReturnsToNormalMode()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));
        System.Threading.Thread.Sleep(35);

        prompt.OnKey(Key(ConsoleKey.Escape));

        Assert.Equal(PromptMode.Normal, prompt.Mode);
    }

    [Fact]
    public void Backspace_OnEmptyShellPrompt_ReturnsToNormalMode()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));
        System.Threading.Thread.Sleep(35);

        prompt.OnKey(Key(ConsoleKey.Backspace));

        Assert.Equal(PromptMode.Normal, prompt.Mode);
        Assert.Equal(string.Empty, prompt.Text);
    }

    [Fact]
    public void Backspace_DeletesTextBeforeLeavingShellMode()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));
        System.Threading.Thread.Sleep(35);
        Type(prompt, "ls");

        prompt.OnKey(Key(ConsoleKey.Backspace));
        Assert.Equal(PromptMode.Shell, prompt.Mode);
        Assert.Equal("l", prompt.Text);

        prompt.OnKey(Key(ConsoleKey.Backspace));
        Assert.Equal(PromptMode.Shell, prompt.Mode);
        Assert.Equal(string.Empty, prompt.Text);

        prompt.OnKey(Key(ConsoleKey.Backspace));
        Assert.Equal(PromptMode.Normal, prompt.Mode);
    }

    [Fact]
    public void Enter_SubmitsTheCommandAndStaysInShellMode()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));
        System.Threading.Thread.Sleep(35);
        Type(prompt, "git status");

        var submitted = prompt.OnKey(Enter());

        Assert.Equal("git status", submitted);
        Assert.Equal(string.Empty, prompt.Text);
        // Staying armed makes shell mode usable as a REPL; the user leaves it explicitly.
        Assert.Equal(PromptMode.Shell, prompt.Mode);
    }

    [Fact]
    public void ShellMode_PreservesQuotesPipesRedirectsAndUnicode()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));
        System.Threading.Thread.Sleep(35);

        const string command = "grep -R \"café | 你好\" src/ 2>/dev/null | head -3 > /tmp/out.txt";
        Type(prompt, command);

        Assert.Equal(command, prompt.Text);
        Assert.Equal(command, prompt.OnKey(Enter()));
    }

    [Fact]
    public void ShellMode_CarriesMultilinePasteThrough()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));
        System.Threading.Thread.Sleep(35);

        prompt.InsertText("for f in *.cs; do\r\n  echo \"$f\"\r\ndone");

        Assert.Equal(PromptMode.Shell, prompt.Mode);
        Assert.Equal("for f in *.cs; do\n  echo \"$f\"\ndone", prompt.Text);
    }

    [Fact]
    public void SetShellModeAvailableFalse_MakesBangAnOrdinaryCharacter()
    {
        var prompt = new PromptLine();
        prompt.SetShellModeAvailable(false);

        prompt.OnKey(Char('!'));

        Assert.Equal(PromptMode.Normal, prompt.Mode);
        Assert.Equal("!", prompt.Text);
        Assert.False(prompt.TryEnterShellMode());
    }

    [Fact]
    public void SetShellModeAvailableFalse_LeavesShellModeIfSomehowActive()
    {
        var prompt = new PromptLine();
        prompt.OnKey(Char('!'));
        Assert.Equal(PromptMode.Shell, prompt.Mode);

        prompt.SetShellModeAvailable(false);

        Assert.Equal(PromptMode.Normal, prompt.Mode);
    }

    [Fact]
    public void TryEnterShellMode_RefusesWhenPromptHasText()
    {
        var prompt = new PromptLine();
        Type(prompt, "hello");

        Assert.False(prompt.TryEnterShellMode());
        Assert.Equal(PromptMode.Normal, prompt.Mode);
    }
}
