using System;
using System.Linq;
using Andy.Cli.Services.Sessions;
using Andy.Engine;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Point-in-time and full-session forks (issue #285): boundary semantics, lineage, and the
/// guarantee that continuing one branch cannot mutate the other.
/// </summary>
public class SessionForkerTests : SessionArchiveTestBase
{
    public SessionForkerTests() : base("fork") { }

    private string SaveSession(int turns, string? title = null, SessionUsage? usage = null)
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(turns), "anthropic", "claude-sonnet-4-6",
            new SessionSaveOptions { Title = title, Usage = usage });
        return id;
    }

    [Fact]
    public void ForkAtTurnN_ContainsExactlyTheHistoryBeforeThatTurn()
    {
        var sourceId = SaveSession(5);

        var fork = SessionForker.Fork(Store, sourceId, atTurn: 3);

        Assert.Equal(2, fork.TurnCount);
        Assert.Equal(3, fork.ForkedAtTurn);

        var forked = Store.Load(fork.SessionId)!;
        var turns = forked.Snapshot.Turns!;
        Assert.Equal(2, turns.Count);
        Assert.Equal("question 1", turns[0].User!.Content);
        Assert.Equal("question 2", turns[1].User!.Content);
        Assert.DoesNotContain(turns, t => t.User!.Content == "question 3");
    }

    [Fact]
    public void ForkWithoutBoundary_CopiesTheWholeSession()
    {
        var sourceId = SaveSession(4);

        var fork = SessionForker.Fork(Store, sourceId);

        Assert.Equal(4, fork.TurnCount);
        Assert.Null(fork.ForkedAtTurn);
        Assert.Equal(4, Store.Load(fork.SessionId)!.Snapshot.Turns!.Count);
    }

    [Fact]
    public void ForkBeyondTheLastTurn_IsTreatedAsAFullFork()
    {
        var sourceId = SaveSession(3);

        var fork = SessionForker.Fork(Store, sourceId, atTurn: 99);

        Assert.Equal(3, fork.TurnCount);
        Assert.Null(fork.ForkedAtTurn);
    }

    [Fact]
    public void ForkGetsANewIdAndPreservesLineage()
    {
        var sourceId = SaveSession(3, title: "Root work");

        var fork = SessionForker.Fork(Store, sourceId, atTurn: 2);

        Assert.NotEqual(sourceId, fork.SessionId);
        Assert.True(SessionStore.IsValidSessionId(fork.SessionId));

        var lineage = Store.Load(fork.SessionId)!.Summary.Lineage!;
        Assert.Equal(sourceId, lineage.ParentSessionId);
        Assert.Equal(sourceId, lineage.RootSessionId);
        Assert.Equal(2, lineage.ForkedAtTurn);
        Assert.NotNull(lineage.ForkedUtc);
    }

    [Fact]
    public void ForkOfAFork_KeepsTheOriginalRoot()
    {
        var rootId = SaveSession(6);
        var first = SessionForker.Fork(Store, rootId, atTurn: 5);
        var second = SessionForker.Fork(Store, first.SessionId, atTurn: 3);

        var lineage = Store.Load(second.SessionId)!.Summary.Lineage!;
        Assert.Equal(first.SessionId, lineage.ParentSessionId);
        Assert.Equal(rootId, lineage.RootSessionId);
        Assert.Equal(rootId, second.RootSessionId);
    }

    [Fact]
    public void ContinuingAFork_DoesNotMutateTheSourceTranscript()
    {
        var sourceId = SaveSession(4);
        var sourceBefore = Store.Load(sourceId)!;
        var fork = SessionForker.Fork(Store, sourceId, atTurn: 3);

        // The fork continues with two extra turns of its own.
        var continued = Store.Load(fork.SessionId)!.Snapshot;
        var extended = new TranscriptSnapshot
        {
            Version = continued.Version,
            Turns = continued.Turns!
                .Concat(new[]
                {
                    new TranscriptTurn
                    {
                        User = SessionArchiveTestData.Message("user", "a different direction"),
                        Interleaved = Array.Empty<TranscriptMessage>(),
                        FinalAssistant = SessionArchiveTestData.Message("assistant", "sure")
                    }
                })
                .ToArray()
        };
        Store.Save(fork.SessionId, extended, "anthropic", "claude-sonnet-4-6");

        var sourceAfter = Store.Load(sourceId)!;
        Assert.Equal(sourceBefore.Snapshot.Turns!.Count, sourceAfter.Snapshot.Turns!.Count);
        Assert.Equal(4, sourceAfter.Snapshot.Turns.Count);
        Assert.DoesNotContain(sourceAfter.Snapshot.Turns,
            t => t.User!.Content == "a different direction");
        Assert.Equal(3, Store.Load(fork.SessionId)!.Snapshot.Turns!.Count);
    }

    [Fact]
    public void ForkLivesInItsOwnFile_SoRewritingTheSourceLeavesItAlone()
    {
        var sourceId = SaveSession(3);
        var fork = SessionForker.Fork(Store, sourceId);

        // Completely replace the source transcript.
        Store.Save(sourceId, SessionArchiveTestData.Snapshot(1), "anthropic", "claude-sonnet-4-6");

        var forked = Store.Load(fork.SessionId)!;
        Assert.Equal(3, forked.Snapshot.Turns!.Count);
        Assert.Equal("question 3", forked.Snapshot.Turns[2].User!.Content);
        Assert.Single(Store.Load(sourceId)!.Snapshot.Turns!);
    }

    [Fact]
    public void ForkAtTurnOne_IsRejected()
    {
        var sourceId = SaveSession(3);

        var ex = Assert.Throws<SessionArchiveException>(() => SessionForker.Fork(Store, sourceId, atTurn: 1));
        Assert.Contains("--at must be between 2", ex.Message);
        Assert.Single(SessionArchiveTestData.SessionFiles(StoreDirectory));
    }

    [Fact]
    public void ForkOfMissingSession_IsRejected()
    {
        var missing = SessionStore.NewSessionId();
        Assert.Throws<SessionArchiveException>(() => SessionForker.Fork(Store, missing));
    }

    [Fact]
    public void ForkGetsADefaultDiscoverableTitle()
    {
        var sourceId = SaveSession(3, title: "Refactor plan");

        var fork = SessionForker.Fork(Store, sourceId, atTurn: 2);

        Assert.Equal("Fork of Refactor plan (before turn 2)", fork.Title);
        Assert.Equal(fork.Title, Store.Load(fork.SessionId)!.Summary.Title);
    }

    [Fact]
    public void ForkAcceptsAnExplicitTitle()
    {
        var sourceId = SaveSession(3);
        var fork = SessionForker.Fork(Store, sourceId, atTurn: 2, title: "Alternative approach");

        Assert.Equal("Alternative approach", Store.Load(fork.SessionId)!.Summary.Title);
    }

    [Fact]
    public void PartialFork_StartsWithUnknownRatherThanZeroUsage()
    {
        var usage = new SessionUsage { InputTokens = 1000, OutputTokens = 500, EstimatedCostUsd = 0.01m };
        var sourceId = SaveSession(4, usage: usage);

        var partial = SessionForker.Fork(Store, sourceId, atTurn: 3);
        var full = SessionForker.Fork(Store, sourceId);

        // A partial fork covers only part of the source's traffic, so inheriting the
        // source totals would overstate it: usage is left unrecorded (unknown).
        Assert.Null(Store.Load(partial.SessionId)!.Summary.Usage);
        // A full fork is the same conversation, so the totals carry over.
        Assert.Equal(1000, Store.Load(full.SessionId)!.Summary.Usage!.InputTokens);
    }
}
