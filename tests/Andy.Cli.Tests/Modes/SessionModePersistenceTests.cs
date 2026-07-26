using System;
using System.IO;
using Andy.Cli.Modes;
using Andy.Cli.Services.Sessions;
using Andy.Engine;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// Mode persistence and restore (issue #278). A planning session must come back as a planning
/// session; a session file written before modes existed must not be read as "Build".
/// </summary>
public sealed class SessionModePersistenceTests : IDisposable
{
    private readonly string _directory;
    private readonly SessionStore _store;

    public SessionModePersistenceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "andy-mode-sessions-" + Guid.NewGuid().ToString("N")[..8]);
        _store = new SessionStore(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test hygiene only.
        }
    }

    private static TranscriptSnapshot Snapshot() => new()
    {
        Turns = new[]
        {
            new TranscriptTurn
            {
                User = new TranscriptMessage
                {
                    Role = "user",
                    Content = "plan the refactor",
                    Timestamp = DateTimeOffset.UtcNow,
                    Id = Guid.NewGuid().ToString("N"),
                },
                Interleaved = Array.Empty<TranscriptMessage>(),
                FinalAssistant = new TranscriptMessage
                {
                    Role = "assistant",
                    Content = "here is the plan",
                    Timestamp = DateTimeOffset.UtcNow,
                    Id = Guid.NewGuid().ToString("N"),
                },
            },
        },
    };

    [Fact]
    public void SavedModeIsRoundTripped()
    {
        Assert.True(_store.Save("s1", Snapshot(), "anthropic", "claude", AgentModeCatalog.PlanId));

        var record = _store.Load("s1");

        Assert.NotNull(record);
        Assert.Equal("plan", record!.Summary.Mode);
        Assert.True(AgentModeCatalog.TryParse(record.Summary.Mode, out var definition));
        Assert.Equal(AgentMode.Plan, definition!.Mode);
    }

    [Fact]
    public void BuildModeIsRoundTrippedToo()
    {
        _store.Save("s2", Snapshot(), "anthropic", "claude", AgentModeCatalog.BuildId);

        Assert.Equal("build", _store.Load("s2")!.Summary.Mode);
    }

    [Fact]
    public void ModeAppearsInTheListing()
    {
        _store.Save("s3", Snapshot(), "anthropic", "claude", AgentModeCatalog.PlanId);

        var summary = Assert.Single(_store.List());
        Assert.Equal("plan", summary.Mode);
    }

    [Fact]
    public void SessionsWrittenWithoutAMode_LoadAsUnset_NotAsBuild()
    {
        // Omitting the mode must be distinguishable from recording "build", so restore can leave
        // the current mode alone instead of downgrading a planning session.
        _store.Save("s4", Snapshot(), "anthropic", "claude");

        var record = _store.Load("s4");

        Assert.Equal(string.Empty, record!.Summary.Mode);
        Assert.False(AgentModeCatalog.TryParse(record.Summary.Mode, out _));
    }

    [Fact]
    public void RestoringAPlanSession_PutsTheStateIntoPlanMode()
    {
        _store.Save("s5", Snapshot(), "anthropic", "claude", AgentModeCatalog.PlanId);
        var record = _store.Load("s5")!;
        var state = new AgentModeState(AgentMode.Build);

        Assert.True(AgentModeCatalog.TryParse(record.Summary.Mode, out var saved));
        Assert.True(state.TrySet(saved!.Mode, ModeChangeSource.SessionRestore, out _));
        Assert.Equal(AgentMode.Plan, state.Current);
    }

    [Fact]
    public void RestoringABuildSession_CannotSilentlyLeavePlanMode()
    {
        _store.Save("s6", Snapshot(), "anthropic", "claude", AgentModeCatalog.BuildId);
        var record = _store.Load("s6")!;
        var state = new AgentModeState(AgentMode.Plan);

        Assert.True(AgentModeCatalog.TryParse(record.Summary.Mode, out var saved));
        Assert.False(state.TrySet(saved!.Mode, ModeChangeSource.SessionRestore, out var error));
        Assert.NotNull(error);
        Assert.Equal(AgentMode.Plan, state.Current);
    }
}
