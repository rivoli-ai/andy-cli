using System;
using System.Linq;
using Andy.Cli.Services.Sessions;
using Andy.Engine;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Feed replay of a resumed session (issue #231): user and assistant messages are
/// replayed in order, tool activity is summarized, interrupted turns get a notice.
/// </summary>
public class SessionReplayFormatterTests
{
    private static TranscriptMessage Message(string role, string? content,
        int toolCalls = 0) => new()
        {
            Role = role,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow,
            Id = Guid.NewGuid().ToString("N"),
            ToolCalls = toolCalls == 0
                ? null
                : Enumerable.Range(0, toolCalls)
                    .Select(i => new TranscriptToolCall { Id = $"call{i}", Name = "read_file", ArgumentsJson = "{}" })
                    .ToArray()
        };

    [Fact]
    public void Format_ReplaysUserAndAssistantInOrder()
    {
        var snapshot = new TranscriptSnapshot
        {
            Turns = new[]
            {
                new TranscriptTurn
                {
                    User = Message("user", "Question one"),
                    Interleaved = Array.Empty<TranscriptMessage>(),
                    FinalAssistant = Message("assistant", "Answer one")
                },
                new TranscriptTurn
                {
                    User = Message("user", "Question two"),
                    Interleaved = Array.Empty<TranscriptMessage>(),
                    FinalAssistant = Message("assistant", "Answer two")
                }
            }
        };

        var entries = SessionReplayFormatter.Format(snapshot);

        Assert.Equal(4, entries.Count);
        Assert.Equal(SessionReplayFormatter.EntryKind.User, entries[0].Kind);
        Assert.Equal("Question one", entries[0].Text);
        Assert.Equal(SessionReplayFormatter.EntryKind.Assistant, entries[1].Kind);
        Assert.Equal("Answer one", entries[1].Text);
        Assert.Equal("Question two", entries[2].Text);
        Assert.Equal("Answer two", entries[3].Text);
    }

    [Fact]
    public void Format_SummarizesToolActivityAndKeepsNarration()
    {
        var snapshot = new TranscriptSnapshot
        {
            Turns = new[]
            {
                new TranscriptTurn
                {
                    User = Message("user", "Read that file"),
                    Interleaved = new[]
                    {
                        Message("assistant", "Let me read it first.", toolCalls: 2),
                        Message("tool", "raw tool payload should NOT be replayed")
                    },
                    FinalAssistant = Message("assistant", "It contains X.")
                }
            }
        };

        var entries = SessionReplayFormatter.Format(snapshot);

        Assert.Equal(4, entries.Count);
        Assert.Equal("Read that file", entries[0].Text);
        Assert.Equal("Let me read it first.", entries[1].Text);
        Assert.Equal(SessionReplayFormatter.EntryKind.Notice, entries[2].Kind);
        Assert.Equal("[2 tool calls executed]", entries[2].Text);
        Assert.Equal("It contains X.", entries[3].Text);
        Assert.DoesNotContain(entries, e => e.Text.Contains("raw tool payload"));
    }

    [Fact]
    public void Format_MarksTurnsWithoutFinalAnswer()
    {
        var snapshot = new TranscriptSnapshot
        {
            Turns = new[]
            {
                new TranscriptTurn
                {
                    User = Message("user", "Interrupted question"),
                    Interleaved = Array.Empty<TranscriptMessage>(),
                    FinalAssistant = null
                }
            }
        };

        var entries = SessionReplayFormatter.Format(snapshot);

        Assert.Equal(2, entries.Count);
        Assert.Equal(SessionReplayFormatter.EntryKind.Notice, entries[1].Kind);
        Assert.Equal("[turn ended without a final answer]", entries[1].Text);
    }

    [Fact]
    public void Format_EmptySnapshotYieldsNoEntries()
    {
        Assert.Empty(SessionReplayFormatter.Format(new TranscriptSnapshot()));
    }
}
