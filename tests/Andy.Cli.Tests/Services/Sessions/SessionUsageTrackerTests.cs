using Andy.Cli.Services;
using Andy.Cli.Services.Sessions;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

public class SessionUsageTrackerTests : SessionArchiveTestBase
{
    public SessionUsageTrackerTests() : base("usage-tracker") { }

    [Fact]
    public async Task ResumedServicePreservesPriorUsageAcrossCounterResetsAndRepeatedSaves()
    {
        var original = new SessionUsage
        {
            InputTokens = 1000,
            OutputTokens = 200,
            ReasoningTokens = 30,
            CacheReadTokens = 400,
            CacheWriteTokens = 100,
            EstimatedCostUsd = 2.5m
        };
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(1), "previous", "previous",
            new SessionSaveOptions { Usage = original });
        var saved = Store.Load(id)!;
        var usage = new SessionUsageTracker();
        usage.Restore(saved.Summary.Usage);
        var counter = new TokenCounter();
        var provider = new Mock<ILlmProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                AssistantMessage = new Message { Role = Role.Assistant, Content = "Done." },
                FinishReason = "stop",
                Usage = new LlmUsage { PromptTokens = 10, CompletionTokens = 5 }
            });
        var registry = new Mock<IToolRegistry>();
        registry.SetupGet(r => r.Tools).Returns(Array.Empty<ToolRegistration>());
        registry.Setup(r => r.GetTools(It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>())).Returns(Array.Empty<ToolRegistration>());
        using var service = new SimpleAssistantService(provider.Object, registry.Object, Mock.Of<IToolExecutor>(),
            new FeedView(), "llama3", "ollama", counter, sessionUsage: usage);
        service.RestoreTranscript(saved.Snapshot);
        for (int turn = 1; turn <= 2; turn++)
        {
            await service.ProcessMessageAsync("Continue");
            counter.Reset();
            for (int save = 0; save < 2; save++)
                Store.Save(id, service.ExportTranscript(), "ollama", "llama3", new SessionSaveOptions { Usage = usage.GetSnapshot() });
            Assert.Equal(original with { InputTokens = 1000 + 10 * turn, OutputTokens = 200 + 5 * turn }, Store.Load(id)!.Summary.Usage);
        }
    }

    [Fact]
    public void NewSessionsAndSessionSwitchesDoNotInheritPreviousTotals()
    {
        var usage = new SessionUsageTracker();
        Assert.Null(usage.GetSnapshot());
        usage.Restore(new SessionUsage { InputTokens = 50, EstimatedCostUsd = 1m });
        usage.Restore(new SessionUsage { InputTokens = 7 });
        Assert.Equal(7, usage.GetSnapshot()!.InputTokens);
        usage.Restore(null);
        Assert.Null(usage.GetSnapshot());
        usage.Record(new LlmUsage { PromptTokens = 2 }, "ollama", "llama3");
        Assert.Equal(2, usage.GetSnapshot()!.InputTokens);
        Assert.Equal(0m, usage.GetSnapshot()!.EstimatedCostUsd);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownPricingRemainsUnknownAcrossModelChanges(bool unknownFirst)
    {
        var usage = new SessionUsageTracker();
        foreach (bool unknown in new[] { unknownFirst, !unknownFirst })
            usage.Record(new LlmUsage { PromptTokens = 10 }, unknown ? "unpriced" : "ollama", "model");
        Assert.Equal(20, usage.GetSnapshot()!.InputTokens);
        Assert.Null(usage.GetSnapshot()!.EstimatedCostUsd);
    }
}
