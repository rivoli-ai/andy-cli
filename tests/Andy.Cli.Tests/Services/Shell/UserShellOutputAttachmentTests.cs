using System;
using Andy.Cli.Services.Sessions;
using Andy.Cli.Services.Shell;
using Xunit;

namespace Andy.Cli.Tests.Services.Shell;

/// <summary>
/// The explicit follow-up action for shell escape (issue #286). Output from a user-invoked command
/// never reaches the model on its own; <c>/attach</c> is the only path, it is always user-initiated,
/// and what it produces is redacted first because - unlike the feed - a prompt leaves the machine.
/// </summary>
public class UserShellOutputAttachmentTests
{
    private static UserShellCommandResult Result(
        string command = "echo hi",
        string stdout = "hi\n",
        string stderr = "",
        int? exitCode = 0,
        UserShellOutcome outcome = UserShellOutcome.Succeeded)
        => new(
            Command: command,
            Outcome: outcome,
            ExitCode: exitCode,
            StandardOutput: stdout,
            StandardError: stderr,
            Duration: TimeSpan.FromMilliseconds(12),
            WorkingDirectory: "/work",
            TimedOut: false,
            StandardOutputTruncated: 0,
            StandardErrorTruncated: 0,
            ErrorMessage: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch);

    private static UserShellOutputAttachment Buffer()
        => new(new SessionRedactor(Array.Empty<string>()));

    [Fact]
    public void EmptyBuffer_HasNothingToAttach()
    {
        var buffer = Buffer();

        Assert.Equal(0, buffer.Count);
        Assert.Null(buffer.Peek());
        Assert.Null(buffer.BuildAttachment());
        Assert.Empty(buffer.DescribeAvailable());
    }

    [Fact]
    public void Peek_IndexesBackwardsFromTheMostRecentCommand()
    {
        var buffer = Buffer();
        buffer.Record(Result(command: "first"));
        buffer.Record(Result(command: "second"));

        Assert.Equal("second", buffer.Peek(1)!.Command);
        Assert.Equal("first", buffer.Peek(2)!.Command);
        Assert.Null(buffer.Peek(3));
        Assert.Null(buffer.Peek(0));
    }

    [Fact]
    public void Record_DropsTheOldestOnceCapacityIsReached()
    {
        var buffer = Buffer();
        for (var i = 0; i < UserShellOutputAttachment.Capacity + 5; i++)
        {
            buffer.Record(Result(command: "cmd" + i));
        }

        Assert.Equal(UserShellOutputAttachment.Capacity, buffer.Count);
        Assert.Equal("cmd" + (UserShellOutputAttachment.Capacity + 4), buffer.Peek(1)!.Command);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var buffer = Buffer();
        buffer.Record(Result());

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void DescribeAvailable_ListsNewestFirstWithStatusAndSize()
    {
        var buffer = Buffer();
        buffer.Record(Result(command: "older"));
        buffer.Record(Result(command: "newer", exitCode: 3, outcome: UserShellOutcome.Failed));

        var lines = buffer.DescribeAvailable();

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("1. newer", lines[0]);
        Assert.Contains("exit 3", lines[0]);
        Assert.StartsWith("2. older", lines[1]);
    }

    [Fact]
    public void BuildAttachment_AttributesTheCommandToTheUser()
    {
        var buffer = Buffer();
        buffer.Record(Result(command: "git status", stdout: "clean\n"));

        var attachment = buffer.BuildAttachment()!;

        // The model must never mistake this for something it invoked and "helpfully" re-run it.
        Assert.Contains("I ran myself", attachment);
        Assert.Contains("$ git status", attachment);
        Assert.Contains("clean", attachment);
        Assert.Contains("/work", attachment);
    }

    [Fact]
    public void BuildAttachment_RedactsSecretsBeforeTheyCanReachTheModel()
    {
        var fakeKey = string.Concat("sk", "-", "abcdefghijklmnop");
        var buffer = Buffer();
        buffer.Record(Result(command: $"echo {fakeKey}", stdout: fakeKey + "\n"));

        var attachment = buffer.BuildAttachment()!;

        Assert.DoesNotContain(fakeKey, attachment);
        Assert.Contains(SessionRedactor.Replacement, attachment);
        // The buffer itself still holds the verbatim result, which is what the feed shows.
        Assert.Contains(fakeKey, buffer.Peek()!.StandardOutput);
    }

    [Fact]
    public void BuildAttachment_LabelsStderrSeparately()
    {
        var buffer = Buffer();
        buffer.Record(Result(stdout: "out", stderr: "bad thing", exitCode: 1, outcome: UserShellOutcome.Failed));

        var attachment = buffer.BuildAttachment()!;

        Assert.Contains("[stderr]", attachment);
        Assert.Contains("bad thing", attachment);
        Assert.Contains("exit 1", attachment);
    }

    [Fact]
    public void BuildAttachment_MarksAnEmptyResultExplicitly()
    {
        var buffer = Buffer();
        buffer.Record(Result(stdout: "", stderr: ""));

        Assert.Contains("(no output)", buffer.BuildAttachment()!);
    }

    [Fact]
    public void BuildAttachment_FencesOutputThatContainsItsOwnBackticks()
    {
        // A naive three-backtick fence would be closed early by the output, spilling the rest into
        // the prompt as prose.
        var buffer = Buffer();
        buffer.Record(Result(stdout: "before\n```\nstill output\n```\nafter\n"));

        var attachment = buffer.BuildAttachment()!;

        Assert.Contains("````", attachment);
        Assert.Contains("still output", attachment);
        Assert.Contains("after", attachment);
    }

    [Fact]
    public void BuildAttachment_TrimsOutputThatWouldSwampThePrompt()
    {
        var buffer = Buffer();
        buffer.Record(Result(stdout: new string('x', UserShellOutputAttachment.MaxAttachedCharacters + 500)));

        var attachment = buffer.BuildAttachment()!;

        Assert.Contains("attachment truncated", attachment);
        Assert.True(attachment.Length < UserShellOutputAttachment.MaxAttachedCharacters + 500);
    }

    [Fact]
    public void BuildAttachment_WithAnUnknownIndex_ReturnsNull()
    {
        var buffer = Buffer();
        buffer.Record(Result());

        Assert.Null(buffer.BuildAttachment(2));
        Assert.Null(buffer.BuildAttachment(-1));
    }
}
