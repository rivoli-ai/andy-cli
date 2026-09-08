using Andy.Cli.Commands;
using Andy.Cli.Headless;
using Andy.Cli.Hosting;
using Andy.Cli.Services.Sessions;
using Andy.Engine;
using System.Text.Json;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

public class AgentIdentityPersistenceTests : SessionArchiveTestBase
{
    public AgentIdentityPersistenceTests() : base("identity") { }

    [Fact]
    public void AcpSessionsAreIndependentAndExplicitNameOverridesRestoredName()
    {
        var registry = new Moq.Mock<Andy.Tools.Core.IToolRegistry>();
        registry.SetupGet(r => r.Tools).Returns(Array.Empty<Andy.Tools.Core.ToolRegistration>());
        var factory = new Andy.Cli.ACP.SimpleAgentSessionAgentFactory(
            Moq.Mock.Of<Andy.Model.Llm.ILlmProvider>(), registry.Object,
            Moq.Mock.Of<Andy.Tools.Core.IToolExecutor>(), null, agentName: "current");
        using var first = factory.Create("system", "test", "test");
        using var second = factory.Create("system", "test", "test");
        Assert.NotEqual(first.ExportTranscript().Identity!.AgentId, second.ExportTranscript().Identity!.AgentId);
        var source = new AgentIdentityState();
        source.SetName("previous");
        first.RestoreTranscript(new TranscriptSnapshot { Identity = source.GetSnapshot() });
        var restored = first.ExportTranscript().Identity!;
        Assert.Equal(source.GetSnapshot().AgentId, restored.AgentId);
        Assert.Equal("current", restored.Name);
        Assert.Equal(new[] { "created", "renamed", "resumed", "renamed" }, restored.History.Select(e => e.Kind));
        Assert.Equal("current", second.ExportTranscript().Identity!.Name);
    }

    [Fact]
    public void NamesSurviveSaveExportImportAndForkWithIndependentResumeActivations()
    {
        var identity = new AgentIdentityState();
        identity.SetName("cedar");
        identity.SetName("oak");
        var snapshot = SessionArchiveTestData.Snapshot(2) with { Identity = identity.GetSnapshot() };
        var id = SessionStore.NewSessionId();
        Store.Save(id, snapshot, "test", "test");
        SessionArchiveExporter.Export(Store, id, WorkPath("agent.json"), new SessionRedactor(Array.Empty<string>()));
        var imported = SessionArchiveImporter.ImportFile(Store, WorkPath("agent.json"));
        var forked = SessionForker.Fork(Store, imported.SessionId, atTurn: 2);
        foreach (var savedId in new[] { id, imported.SessionId, forked.SessionId })
        {
            var saved = Store.Load(savedId)!.Snapshot.Identity!;
            Assert.Equal(snapshot.Identity.AgentId, saved.AgentId);
            Assert.Equal("oak", saved.Name);
            Assert.Equal(snapshot.Identity.History, saved.History);
            var resumed = new AgentIdentityState();
            resumed.Restore(saved);
            Assert.NotEqual(saved.Activation.InstanceId, resumed.GetSnapshot().Activation.InstanceId);
            Assert.Equal("resumed", resumed.GetSnapshot().History[^1].Kind);
        }
    }

    [Fact]
    public void InteractiveRenameClearAndInspectPreserveHistory()
    {
        var identity = new AgentIdentityState();
        Assert.Contains("cedar", AgentNameCommand.Execute(identity, "cedar"));
        Assert.Contains(identity.GetSnapshot().AgentId, AgentNameCommand.Execute(identity, ""));
        Assert.Contains("(unnamed)", AgentNameCommand.Execute(identity, "--clear"));
        using var history = JsonDocument.Parse(AgentNameCommand.Execute(identity, "--history"));
        Assert.Equal(3, history.RootElement.GetProperty("history").GetArrayLength());
        Assert.Throws<ArgumentException>(() => AgentNameCommand.Execute(identity, "bad\nname"));
    }

    [Fact]
    public void StartupNameIsExtractedWithoutConsumingPromptArguments()
    {
        var parsed = AgentNameOption.Extract(new[] { "run", "--agent-name", " cedar ", "--", "--agent-name=prompt text" });
        Assert.Equal("cedar", parsed.Name);
        Assert.Equal(new[] { "run", "--", "--agent-name=prompt text" }, parsed.Args);
        Assert.Equal("", AgentNameOption.Extract(new[] { "--agent-name=" }).Name);
        Assert.Null(AgentNameOption.Extract(new[] { "run" }).Name);
        Assert.Throws<ArgumentException>(() => AgentNameOption.Extract(new[] { "--agent-name" }));
        Assert.Throws<ArgumentException>(() => AgentNameOption.Extract(new[] { "--agent-name=a", "--agent-name=b" }));
        Assert.Throws<ArgumentException>(() => AgentNameOption.Extract(new[] { "--agent-name=bad\nname" }));
    }

    [Fact]
    public void HeadlessMetadataReflectsRenamingDuringRun()
    {
        var identity = new AgentIdentityState();
        identity.SetName("cedar");
        using var writer = new StringWriter();
        var emitter = new HeadlessEventEmitter(writer) { AgentIdentity = identity };
        emitter.EmitStarted(Guid.NewGuid(), "slug", "test", "test", 2);
        identity.SetName("oak");
        emitter.EmitFinished(0, 10, 1);
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var started = JsonDocument.Parse(lines[0]);
        using var finished = JsonDocument.Parse(lines[1]);
        var first = started.RootElement.GetProperty("data").GetProperty("agent_identity");
        var last = finished.RootElement.GetProperty("data").GetProperty("agent_identity");
        Assert.Equal("cedar", first.GetProperty("name").GetString());
        Assert.Equal("oak", last.GetProperty("name").GetString());
        Assert.Equal(first.GetProperty("agent_id").GetString(), last.GetProperty("agent_id").GetString());
        Assert.Equal(3, last.GetProperty("history").GetArrayLength());
    }
}
