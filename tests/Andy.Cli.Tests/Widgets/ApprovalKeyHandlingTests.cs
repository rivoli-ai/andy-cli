using System;
using System.Linq;
using Andy.Cli.Widgets;
using Andy.Permissions.Model;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// A single bare keystroke used to resolve the approval prompt, which is what let a command be
/// denied that the user never saw a prompt for: the prompt appears asynchronously while the user
/// is free to type, so a "d" or "n" inside an ordinary word answered it the instant it appeared.
///
/// Two changes close that off, and both are pinned here: the letter shortcuts now SELECT rather
/// than resolve, so every decision passes through Enter, and the interactive loop drains buffered
/// input when the prompt opens.
/// </summary>
public class ApprovalKeyHandlingTests
{
    private static PermissionRequest Request() => new(
        "execute_command", "Execute Command", "run a command",
        new PermissionEvaluation(PermissionOutcome.Ask, new[]
        {
            new EvaluatedResource(new ResourceAccess(ResourceKind.Command, "dotnet build"),
                PermissionOutcome.Ask, null, true)
        }));

    private static InlineApprovalPrompt Started()
    {
        var prompt = new InlineApprovalPrompt();
        prompt.Begin(Request());
        return prompt;
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    [Theory]
    [InlineData(ConsoleKey.D)]
    [InlineData(ConsoleKey.N)]
    public void LetterShortcutsSelectWithoutDeciding(ConsoleKey key)
    {
        // A stray character must never be an irreversible answer.
        var prompt = Started();

        Assert.Null(prompt.HandleKey(Key(key)));
        Assert.True(prompt.IsActive);
        Assert.Equal(2, prompt.SelectedIndex);   // Deny highlighted, not chosen
    }

    [Theory]
    [InlineData(ConsoleKey.A)]
    [InlineData(ConsoleKey.Y)]
    public void AllowShortcutsAlsoOnlySelect(ConsoleKey key)
    {
        var prompt = Started();

        Assert.Null(prompt.HandleKey(Key(key)));
        Assert.True(prompt.IsActive);
        Assert.Equal(0, prompt.SelectedIndex);   // Allow once highlighted
    }

    [Fact]
    public void SelectingThenConfirmingIsWhatDecides()
    {
        var prompt = Started();
        prompt.HandleKey(Key(ConsoleKey.A));

        var decision = prompt.HandleKey(Key(ConsoleKey.Enter));

        Assert.NotNull(decision);
        Assert.True(decision!.Allowed);
    }

    [Fact]
    public void EscapeStillDeniesOnOnePress()
    {
        // Escape is not a character anyone types by accident, and dismissing a modal with it is
        // universal, so it stays a one-press deny.
        var decision = Started().HandleKey(Key(ConsoleKey.Escape));

        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
    }

    [Fact]
    public void DenyRemainsThePreselectedDefault()
    {
        Assert.Equal(2, Started().SelectedIndex);
    }

    [Fact]
    public void OrdinaryTypingDoesNotResolveThePrompt()
    {
        // Letters with no binding must leave the prompt open rather than answering it.
        var prompt = Started();

        foreach (var key in new[] { ConsoleKey.B, ConsoleKey.Q, ConsoleKey.Z })
        {
            Assert.Null(prompt.HandleKey(Key(key)));
        }

        Assert.True(prompt.IsActive);
    }
}
