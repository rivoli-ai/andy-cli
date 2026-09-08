using System.Text.Json;
using Andy.Acp.Core.Agent;
using Andy.Acp.Core.Transport;
using Andy.Cli.ACP;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Moq;

namespace Andy.Cli.Tests.ACP;

public class AcpPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "acp-persist-" + Guid.NewGuid().ToString("N"));
    private readonly List<LlmRequest> _requests = [];
    private readonly List<string> _wire = [];
    private AcpSessionStore Store => new(_directory);

    private AndyAgentProvider Create()
    {
        var llm = new Mock<ILlmProvider>();
        llm.Setup(p => p.StreamCompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>())).Throws(new NotSupportedException());
        llm.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns((LlmRequest request, CancellationToken _) =>
            {
                _requests.Add(request); return Task.FromResult(new LlmResponse
                { AssistantMessage = new Message { Role = Role.Assistant, Content = "answer" } });
            });
        var registry = new Mock<IToolRegistry>();
        registry.SetupGet(r => r.Tools).Returns(Array.Empty<ToolRegistration>());
        registry.Setup(r => r.GetTools()).Returns(Array.Empty<ToolRegistration>());
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.WriteMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string text, CancellationToken _) => { _wire.Add(text); return Task.CompletedTask; });
        return new AndyAgentProvider(llm.Object, registry.Object, new Mock<IToolExecutor>().Object,
            defaultProvider: "openai", defaultModel: "test-model", sessionStore: Store, historyReplay: new AcpHistoryReplay(transport.Object));
    }

    [Fact]
    public async Task RestartRestoresHistoryAndMetadata_ResumeDoesNotReplay_LoadDoes()
    {
        var streamer = new Mock<IResponseStreamer>();
        string id;
        using (var first = Create())
        {
            var session = await first.CreateSessionAsync(new NewSessionParams { Cwd = _directory, Mode = "assistant" }, default);
            id = session.SessionId;
            var result = await first.ProcessPromptAsync(id, new PromptMessage { Text = "remember alpha" }, streamer.Object, default);
            Assert.Equal(StopReason.Completed, result.StopReason);
        }
        using var second = Create();
        var resumed = await second.ResumeSessionAsync(new LoadSessionParams { SessionId = id, Cwd = _directory }, default);
        Assert.NotNull(resumed);
        Assert.Equal("test-model", resumed.Model);
        Assert.Equal(1, resumed.MessageCount);
        Assert.Empty(_wire);
        streamer.Invocations.Clear();
        await second.LoadSessionAsync(new LoadSessionParams { SessionId = id }, streamer.Object, default);
        using var message = JsonDocument.Parse(Assert.Single(_wire));
        Assert.Equal("user_message_chunk", message.RootElement.GetProperty("params").GetProperty("update").GetProperty("sessionUpdate").GetString());
        streamer.Verify(s => s.SendMessageChunkAsync("answer", It.IsAny<CancellationToken>()), Times.Once);
        await second.ProcessPromptAsync(id, new PromptMessage { Text = "what did I say?" }, streamer.Object, default);
        Assert.Contains(_requests.Last().Messages, m => m.Role == Role.User && m.Content == "remember alpha");
        Assert.Contains(_requests.Last().Messages, m => m.Role == Role.Assistant && m.Content == "answer");
        await second.CloseSessionAsync(id, default);
        Assert.NotNull(Store.Load(id));
        Assert.NotNull(await second.ResumeSessionAsync(new LoadSessionParams { SessionId = id }, default));
        Assert.True(await second.DeleteSessionAsync(id, default));
        Assert.Null(Store.Load(id));
        Assert.Null(await second.ResumeSessionAsync(new LoadSessionParams { SessionId = id }, default));
    }

    [Fact]
    public async Task CatalogFiltersPagesAndRejectsWrongWorkspace()
    {
        for (var i = 0; i < 52; i++) Store.Save(Record("session-" + i, _directory));
        Store.Save(Record("other", Path.GetTempPath()));
        using var provider = Create();
        var page = await provider.ListSessionsAsync(_directory, null, default);
        Assert.Equal(50, page.Sessions.Count);
        Assert.NotNull(page.NextCursor);
        var rest = await provider.ListSessionsAsync(_directory, page.NextCursor, default);
        Assert.Equal(2, rest.Sessions.Count);
        Assert.Null(rest.NextCursor);
        Assert.Empty(page.Sessions.Select(s => s.SessionId).Intersect(rest.Sessions.Select(s => s.SessionId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResumeSessionAsync(
            new LoadSessionParams { SessionId = "session-0", Cwd = Path.GetTempPath() }, default));
    }

    [Fact]
    public void StoreRejectsTraversalAndCorruptionWithoutLosingOtherSessions()
    {
        Assert.Throws<ArgumentException>(() => Store.Load("../escape"));
        Store.Save(Record("good", _directory));
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "invalid JSON");
        Assert.Equal("good", Assert.Single(Store.List()).SessionId);
        Assert.Throws<JsonException>(() => Store.Load("broken"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task ReplayKeepsToolCallCorrelationAndOrder()
    {
        var events = new List<string>();
        var transport = new Mock<ITransport>();
        transport.Setup(t => t.WriteMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) => { events.Add("user"); return Task.CompletedTask; });
        var streamer = new Mock<IResponseStreamer>();
        streamer.Setup(s => s.SendToolCallAsync(It.IsAny<Andy.Acp.Core.Agent.ToolCall>(), It.IsAny<CancellationToken>()))
            .Returns((Andy.Acp.Core.Agent.ToolCall call, CancellationToken _) => { events.Add("call:" + call.Id); return Task.CompletedTask; });
        streamer.Setup(s => s.SendToolResultAsync(It.IsAny<Andy.Acp.Core.Agent.ToolResult>(), It.IsAny<CancellationToken>()))
            .Returns((Andy.Acp.Core.Agent.ToolResult result, CancellationToken _) => { events.Add("result:" + result.CallId); return Task.CompletedTask; });
        streamer.Setup(s => s.SendMessageChunkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string text, CancellationToken _) => { events.Add(text); return Task.CompletedTask; });
        var snapshot = new TranscriptSnapshot
        {
            Turns = [new TranscriptTurn
        {
            User = new() { Role = "user", Content = "read" },
            Interleaved = [new() { Role = "assistant", ToolCalls = [new() { Id = "c1", Name = "read_file" }] },
                new() { Role = "tool", ToolResults = [new() { CallId = "c1", ResultJson = "contents" }] }],
            FinalAssistant = new() { Role = "assistant", Content = "done" }
        }]
        };
        await new AcpHistoryReplay(transport.Object).ReplayAsync("id", snapshot, streamer.Object, default);
        Assert.Equal(new[] { "user", "call:c1", "result:c1", "done" }, events);
    }

    [Fact]
    public void StoreRedactsSecretsBeforeWritingRestorableHistory()
    {
        Store.Save(Record("secret", _directory) with
        {
            Snapshot = new TranscriptSnapshot
            {
                Turns = [new TranscriptTurn
        { User = new() { Role = "user", Content = "api_key=sk-aaaaaaaaaaaaaaaa" } }]
            }
        });
        var text = File.ReadAllText(Path.Combine(_directory, "secret.json"));
        Assert.DoesNotContain("sk-aaaaaaaaaaaaaaaa", text);
        Assert.Contains("[REDACTED]", Store.Load("secret")!.Snapshot.Turns[0].User.Content);
    }

    [Fact]
    public async Task CloseWaitsForPromptCleanupAndPreventsNewPrompts()
    {
        using var entry = new AcpSessionEntry("close-test", null, "assistant", "test");
        var token = entry.BeginPrompt(default);
        var closing = entry.BeginClose();
        Assert.True(token.IsCancellationRequested);
        Assert.False(closing.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => entry.BeginPrompt(default));
        entry.EndPrompt();
        await closing.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static AcpStoredSession Record(string id, string cwd) => new()
    {
        SessionId = id,
        Cwd = cwd,
        Provider = "openai",
        Model = "test",
        Mode = "assistant",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
