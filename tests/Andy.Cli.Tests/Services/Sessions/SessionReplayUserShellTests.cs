using System;
using System.Linq;
using Andy.Cli.Services.Sessions;
using Andy.Engine;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Replay attribution for shell escape (issue #286). A resumed session shows the user's own
/// shell-mode commands interleaved with the conversation, and every one of them is tagged
/// <see cref="SessionReplayFormatter.EntryKind.UserShell"/> - never folded into the model's
/// "N tool calls executed" notice - so nobody reading a replayed or exported transcript can
/// confuse who ran what.
/// </summary>
public class SessionReplayUserShellTests
{
    private static TranscriptMessage Message(string role, string content, DateTimeOffset at, int toolCalls = 0) => new()
    {
        Role = role,
        Content = content,
        Timestamp = at,
        Id = Guid.NewGuid().ToString("N"),
        ToolCalls = Enumerable.Range(0, toolCalls)
            .Select(i => new TranscriptToolCall { Id = $"call{i}", Name = "execute_command", ArgumentsJson = "{}" })
            .ToArray()
    };

    private static UserShellRecord Shell(string command, DateTimeOffset at, string status = "exit 0")
        => new(at, command, 0, status, 5, "/work", "");

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static TranscriptSnapshot TwoTurns() => new()
    {
        Turns = new[]
        {
            new TranscriptTurn
            {
                User = Message("user", "Question one", T0.AddMinutes(1)),
                Interleaved = Array.Empty<TranscriptMessage>(),
                FinalAssistant = Message("assistant", "Answer one", T0.AddMinutes(2))
            },
            new TranscriptTurn
            {
                User = Message("user", "Question two", T0.AddMinutes(5)),
                Interleaved = Array.Empty<TranscriptMessage>(),
                FinalAssistant = Message("assistant", "Answer two", T0.AddMinutes(6))
            }
        }
    };

    [Fact]
    public void Format_WithNoShellCommands_MatchesThePlainOverload()
    {
        var snapshot = TwoTurns();

        var withNull = SessionReplayFormatter.Format(snapshot, null);
        var withEmpty = SessionReplayFormatter.Format(snapshot, Array.Empty<UserShellRecord>());
        var plain = SessionReplayFormatter.Format(snapshot);

        Assert.Equal(plain.Select(e => e.Text), withNull.Select(e => e.Text));
        Assert.Equal(plain.Select(e => e.Text), withEmpty.Select(e => e.Text));
    }

    [Fact]
    public void Format_InterleavesShellCommandsByTimestamp()
    {
        var entries = SessionReplayFormatter.Format(TwoTurns(), new[]
        {
            Shell("git status", T0.AddMinutes(3)),
        });

        var kinds = entries.Select(e => e.Kind).ToArray();
        Assert.Equal(new[]
        {
            SessionReplayFormatter.EntryKind.User,
            SessionReplayFormatter.EntryKind.Assistant,
            SessionReplayFormatter.EntryKind.UserShell,
            SessionReplayFormatter.EntryKind.User,
            SessionReplayFormatter.EntryKind.Assistant,
        }, kinds);
        Assert.Contains("git status", entries[2].Text);
    }

    [Fact]
    public void Format_PlacesCommandsRunAfterTheLastTurnAtTheEnd()
    {
        var entries = SessionReplayFormatter.Format(TwoTurns(), new[]
        {
            Shell("tail -f log", T0.AddMinutes(30)),
        });

        Assert.Equal(SessionReplayFormatter.EntryKind.UserShell, entries[^1].Kind);
        Assert.Contains("tail -f log", entries[^1].Text);
    }

    [Fact]
    public void Format_PlacesCommandsRunBeforeAnyTurnAtTheTop()
    {
        var entries = SessionReplayFormatter.Format(TwoTurns(), new[]
        {
            Shell("pwd", T0),
        });

        Assert.Equal(SessionReplayFormatter.EntryKind.UserShell, entries[0].Kind);
        Assert.Contains("pwd", entries[0].Text);
    }

    [Fact]
    public void Format_SortsOutOfOrderShellRecords()
    {
        var entries = SessionReplayFormatter.Format(TwoTurns(), new[]
        {
            Shell("second", T0.AddMinutes(4)),
            Shell("first", T0.AddSeconds(30)),
        });

        var shellTexts = entries
            .Where(e => e.Kind == SessionReplayFormatter.EntryKind.UserShell)
            .Select(e => e.Text)
            .ToArray();

        Assert.Equal(2, shellTexts.Length);
        Assert.Contains("first", shellTexts[0]);
        Assert.Contains("second", shellTexts[1]);
    }

    [Fact]
    public void Format_NeverCountsUserCommandsAsModelToolCalls()
    {
        // The model made two execute_command calls; the user ran one themselves. The notice must
        // say two, and the user's command must be a separate, differently-kinded entry.
        var snapshot = new TranscriptSnapshot
        {
            Turns = new[]
            {
                new TranscriptTurn
                {
                    User = Message("user", "Build it", T0.AddMinutes(1)),
                    Interleaved = new[] { Message("assistant", "Running the build.", T0.AddMinutes(1), toolCalls: 2) },
                    FinalAssistant = Message("assistant", "Done.", T0.AddMinutes(2))
                }
            }
        };

        var entries = SessionReplayFormatter.Format(snapshot, new[] { Shell("make clean", T0) });

        var notice = Assert.Single(entries, e => e.Kind == SessionReplayFormatter.EntryKind.Notice);
        Assert.Equal("[2 tool calls executed]", notice.Text);

        var shell = Assert.Single(entries, e => e.Kind == SessionReplayFormatter.EntryKind.UserShell);
        Assert.Contains("[user shell]", shell.Text);
        Assert.Contains("make clean", shell.Text);
    }

    [Fact]
    public void Format_ShowsTheOutcomeOfEachUserCommand()
    {
        var entries = SessionReplayFormatter.Format(TwoTurns(), new[]
        {
            Shell("rm -rf /", T0, status: "denied"),
        });

        var shell = Assert.Single(entries, e => e.Kind == SessionReplayFormatter.EntryKind.UserShell);
        Assert.Contains("denied", shell.Text);
    }

    [Fact]
    public void Format_WithAnEmptyTranscript_StillReplaysTheUserCommands()
    {
        var entries = SessionReplayFormatter.Format(
            new TranscriptSnapshot { Turns = Array.Empty<TranscriptTurn>() },
            new[] { Shell("ls", T0) });

        var entry = Assert.Single(entries);
        Assert.Equal(SessionReplayFormatter.EntryKind.UserShell, entry.Kind);
    }
}
