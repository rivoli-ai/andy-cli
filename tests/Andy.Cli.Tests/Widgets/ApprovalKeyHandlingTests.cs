using System;
using System.Linq;
using Andy.Cli.Widgets;
using Andy.Permissions.Model;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// The inline approval prompt resolves on a SINGLE bare keystroke, which is what let a command be
/// denied that the user never saw a prompt for: a character typed while the tool was still running
/// - any word containing "d" or "n" - is consumed the instant the prompt appears and answers it.
///
/// The buffered-input drain lives in the interactive loop, which owns the input source. What is
/// pinned here is the property that makes the drain necessary: these keys are irreversible on
/// first press, with no confirmation step.
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
    [InlineData(ConsoleKey.Escape)]
    public void OneBareKeystrokeDeniesIrreversibly(ConsoleKey key)
    {
        var decision = Started().HandleKey(Key(key));

        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
    }

    [Fact]
    public void OrdinaryTypingDoesNotResolveThePrompt()
    {
        // Letters with no binding must leave the prompt open rather than answering it.
        var prompt = Started();

        foreach (var key in new[] { ConsoleKey.A, ConsoleKey.B, ConsoleKey.Z })
        {
            Assert.Null(prompt.HandleKey(Key(key)));
        }

        Assert.True(prompt.IsActive);
    }
}
