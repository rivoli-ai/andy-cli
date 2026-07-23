using System;
using System.IO;
using System.Linq;
using Andy.Cli.Services.Sessions;
using Andy.Engine;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Session persistence for exit-and-resume (issue #231): id generation, atomic
/// save/load round-trips of the engine's TranscriptSnapshot, listing, and the
/// redaction applied before transcripts reach disk.
/// </summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _directory;

    public SessionStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "andy-session-store-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private SessionStore CreateStore(TimeProvider? clock = null, SessionRedactor? redactor = null) =>
        new(_directory, redactor ?? new SessionRedactor(Array.Empty<string>()), clock);

    private static TranscriptSnapshot MakeSnapshot(
        string userText = "Hello there",
        string assistantText = "Hi! How can I help?")
    {
        return new TranscriptSnapshot
        {
            Turns = new[]
            {
                new TranscriptTurn
                {
                    User = new TranscriptMessage
                    {
                        Role = "user",
                        Content = userText,
                        Timestamp = DateTimeOffset.UtcNow,
                        Id = Guid.NewGuid().ToString("N")
                    },
                    Interleaved = Array.Empty<TranscriptMessage>(),
                    FinalAssistant = new TranscriptMessage
                    {
                        Role = "assistant",
                        Content = assistantText,
                        Timestamp = DateTimeOffset.UtcNow,
                        Id = Guid.NewGuid().ToString("N")
                    }
                }
            }
        };
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void NewSessionId_IsShortFilesystemSafeAndTimeSortable()
    {
        var id = SessionStore.NewSessionId();

        Assert.Matches(@"^\d{8}-\d{6}-[0-9a-f]{4}$", id);
        Assert.True(SessionStore.IsValidSessionId(id));
        Assert.Equal(id, Path.GetFileName(id)); // no path separators sneak in
    }

    [Fact]
    public void NewSessionId_GeneratesDistinctIds()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => SessionStore.NewSessionId()).ToHashSet();
        // Same-second ids differ only by the random suffix; collisions across 50
        // draws of 16 bits are possible but vanishingly unlikely to wipe out all.
        Assert.True(ids.Count > 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    [InlineData("id with spaces")]
    public void IsValidSessionId_RejectsUnsafeIds(string? id)
    {
        Assert.False(SessionStore.IsValidSessionId(id));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsTranscriptAndMetadata()
    {
        var store = CreateStore();
        var snapshot = MakeSnapshot("What is the capital of France?", "Paris.");
        var id = SessionStore.NewSessionId();

        Assert.True(store.Save(id, snapshot, "openai", "gpt-4o"));

        var record = store.Load(id);
        Assert.NotNull(record);
        Assert.Equal(id, record!.Summary.SessionId);
        Assert.Equal("openai", record.Summary.Provider);
        Assert.Equal("gpt-4o", record.Summary.Model);
        Assert.Equal(1, record.Summary.TurnCount);
        Assert.Equal("What is the capital of France?", record.Summary.FirstUserMessage);

        var turn = Assert.Single(record.Snapshot.Turns);
        Assert.Equal("What is the capital of France?", turn.User.Content);
        Assert.Equal("Paris.", turn.FinalAssistant!.Content);
    }

    [Fact]
    public void Save_SkipsEmptyTranscript()
    {
        var store = CreateStore();
        var id = SessionStore.NewSessionId();

        Assert.False(store.Save(id, new TranscriptSnapshot(), "p", "m"));
        Assert.Null(store.Load(id));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Save_RejectsInvalidSessionId()
    {
        var store = CreateStore();
        Assert.Throws<ArgumentException>(() => store.Save("../evil", MakeSnapshot(), "p", "m"));
    }

    [Fact]
    public void Load_ReturnsNullForUnknownId_AndRejectsInvalidId()
    {
        var store = CreateStore();
        Assert.Null(store.Load("20990101-000000-dead"));
        Assert.Throws<ArgumentException>(() => store.Load("../../etc/passwd"));
    }

    [Fact]
    public void Save_RedactsSecretsBeforePersisting()
    {
        var store = CreateStore(redactor: new SessionRedactor(new[] { "super-secret-value-42" }));
        var snapshot = MakeSnapshot(
            userText: "my api_key=sk-abcdef1234567890 please remember it",
            assistantText: "Using Bearer abc.def-token and super-secret-value-42 now.");
        var id = SessionStore.NewSessionId();
        store.Save(id, snapshot, "p", "m");

        var raw = File.ReadAllText(Path.Combine(_directory, id + ".json"));
        Assert.DoesNotContain("sk-abcdef1234567890", raw);
        Assert.DoesNotContain("super-secret-value-42", raw);
        Assert.DoesNotContain("Bearer abc.def-token", raw);
        Assert.Contains("[REDACTED]", raw);

        // And the redacted transcript still restores as a valid snapshot.
        var record = store.Load(id);
        var turn = Assert.Single(record!.Snapshot.Turns);
        Assert.Contains("[REDACTED]", turn.User.Content);
        Assert.Contains("[REDACTED]", turn.FinalAssistant!.Content);
    }

    [Fact]
    public void Save_PreservesCreatedUtcAcrossResaves_AndBumpsUpdatedUtc()
    {
        var clock = new FixedTimeProvider { Now = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero) };
        var store = CreateStore(clock);
        var id = SessionStore.NewSessionId();

        store.Save(id, MakeSnapshot(), "p", "m");
        clock.Now = clock.Now.AddHours(2);
        store.Save(id, MakeSnapshot("Second turn user", "Second answer"), "p", "m");

        var record = store.Load(id)!;
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero), record.Summary.CreatedUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero), record.Summary.UpdatedUtc);
    }

    [Fact]
    public void List_ReturnsNewestFirst_AndLatestMatches()
    {
        var clock = new FixedTimeProvider { Now = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero) };
        var store = CreateStore(clock);

        store.Save("20260723-080000-aaaa", MakeSnapshot("first session"), "p", "m");
        clock.Now = clock.Now.AddMinutes(30);
        store.Save("20260723-083000-bbbb", MakeSnapshot("second session"), "p", "m");
        clock.Now = clock.Now.AddMinutes(30);
        store.Save("20260723-090000-cccc", MakeSnapshot("third session"), "p", "m");

        var sessions = store.List();
        Assert.Equal(
            new[] { "20260723-090000-cccc", "20260723-083000-bbbb", "20260723-080000-aaaa" },
            sessions.Select(s => s.SessionId).ToArray());
        Assert.Equal("third session", sessions[0].FirstUserMessage);
        Assert.Equal("20260723-090000-cccc", store.Latest()!.SessionId);
    }

    [Fact]
    public void List_SkipsCorruptFiles()
    {
        var store = CreateStore();
        store.Save("20260723-100000-aaaa", MakeSnapshot(), "p", "m");
        File.WriteAllText(Path.Combine(_directory, "corrupt.json"), "{ not json !!");

        var sessions = store.List();
        Assert.Single(sessions);
        Assert.Equal("20260723-100000-aaaa", sessions[0].SessionId);
    }

    [Fact]
    public void List_ReturnsEmptyWhenDirectoryMissing()
    {
        var store = CreateStore();
        Assert.Empty(store.List());
        Assert.Null(store.Latest());
    }

    [Fact]
    public void Save_TruncatesLongFirstUserMessageSnippetAndCollapsesNewlines()
    {
        var store = CreateStore();
        var longMessage = "line one\nline two\t" + new string('x', 200);
        var id = SessionStore.NewSessionId();
        store.Save(id, MakeSnapshot(longMessage), "p", "m");

        var summary = store.Load(id)!.Summary;
        Assert.DoesNotContain('\n', summary.FirstUserMessage);
        Assert.True(summary.FirstUserMessage.Length <= 103); // 100 chars + "..."
        Assert.EndsWith("...", summary.FirstUserMessage);
        Assert.StartsWith("line one line two", summary.FirstUserMessage);
    }
}
