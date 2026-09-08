using Andy.Cli.Services.Sessions;
using Andy.Engine;

namespace Andy.Cli.Tests.Services.Sessions;

public class PendingMessageSessionTests : SessionArchiveTestBase
{
    public PendingMessageSessionTests() : base("pending-input") { }

    private SessionRecord SaveFollowup()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, new TranscriptSnapshot
        {
            Turns = new[] { new TranscriptTurn
        {
            User = new TranscriptMessage { Role = "user", Content = "initial" },
            Interleaved = new[]
            {
                new TranscriptMessage { Role = "assistant", Content = "working" },
                new TranscriptMessage { Role = "user", Content = "internal budget nudge" },
                new TranscriptMessage { Role = "user", Content = "change direction", Parts = new[] { new TranscriptPart { Type = "text", Text = "change direction" } } }
            },
            FinalAssistant = new TranscriptMessage { Role = "assistant", Content = "done" }
        } }
        }, "stub", "model");
        return Store.Load(id)!;
    }

    [Fact]
    public void ResumedFeedRetainsFollowupsWithoutAttributingEngineNudgesToTheUser()
    {
        var session = SaveFollowup();
        var entries = SessionReplayFormatter.Format(session.Snapshot);
        Assert.Equal(new[] { "initial", "working", "change direction", "done" }, entries.Select(e => e.Text));
        Assert.Equal(SessionReplayFormatter.EntryKind.User, entries[2].Kind);
    }

    [Fact]
    public void MarkdownExportIncludesFollowupInput()
    {
        var markdown = SessionMarkdownExporter.Render(SaveFollowup());
        Assert.Contains("### User (follow-up)", markdown);
        Assert.Contains("change direction", markdown);
        Assert.DoesNotContain("internal budget nudge", markdown);
    }
}
