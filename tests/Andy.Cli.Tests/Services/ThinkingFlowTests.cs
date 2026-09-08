using Andy.Cli.Instrumentation;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Moq;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Services;

public class ThinkingFlowTests
{
    [Fact]
    public async Task RealAgentPreservesThinkingButHiddenFeedOnlyShowsAnswer()
    {
        var oldVisibility = ThinkingView.Visible;
        const string thought = "internal-step-161";
        const string response = "<think>internal-step-161</think>Final answer";
        try
        {
            ThinkingView.Visible = false;
            var provider = new Mock<ILlmProvider>();
            provider.SetupGet(p => p.Name).Returns("stub");
            provider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LlmResponse { AssistantMessage = new Message { Role = Role.Assistant, Content = response } });
            var registry = new Mock<IToolRegistry>();
            registry.SetupGet(r => r.Tools).Returns(Array.Empty<ToolRegistration>());
            registry.Setup(r => r.GetTools()).Returns(Array.Empty<ToolRegistration>());
            var feed = new FeedView();
            using var service = new SimpleAssistantService(provider.Object, registry.Object, new Mock<IToolExecutor>().Object,
                feed, "model", "stub");
            Assert.Equal(response, await service.ProcessMessageAsync("Explain"));
            Assert.Contains(thought, System.Text.Json.JsonSerializer.Serialize(service.ExportTranscript()));
            Assert.Contains(InstrumentationHub.Instance.GetEventHistory().OfType<ThinkingEvent>(), e => e.Content == thought);
            Assert.DoesNotContain(thought, Render(feed));
            ThinkingView.Visible = true;
            Assert.Contains(thought, Render(feed));
        }
        finally { ThinkingView.Visible = oldVisibility; }
    }

    [Fact]
    public async Task StreamingMetadataInvokesCallbacksAndPreservesChunks()
    {
        var chunk = new LlmStreamResponse
        {
            Delta = new Message
            {
                Role = Role.Assistant,
                Metadata = new() { ["thinking"] = "reasoning" }
            },
            IsComplete = true
        };
        var provider = new Mock<ILlmProvider>();
        provider.Setup(p => p.StreamCompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>())).Returns(Stream(chunk));
        var events = new List<string>();
        var wrapper = new UsageTrackingLlmProvider(provider.Object, _ => { }, null, null,
            () => events.Add("start"), s => events.Add(s), () => events.Add("end"));
        await foreach (var returned in wrapper.StreamCompleteAsync(new LlmRequest { Messages = [] })) Assert.Same(chunk, returned);
        Assert.Equal(new[] { "start", "reasoning", "end" }, events);
    }

    [Fact]
    public async Task RenderingCallbackCannotFailACompletion()
    {
        var response = new LlmResponse { Metadata = new() { ["thinking"] = "step" } };
        var provider = new Mock<ILlmProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        var wrapper = new UsageTrackingLlmProvider(provider.Object, _ => { }, null, null,
            onThinkingText: _ => throw new InvalidOperationException("display failed"));
        Assert.Same(response, await wrapper.CompleteAsync(new LlmRequest { Messages = [] }));
    }

    private static async IAsyncEnumerable<LlmStreamResponse> Stream(LlmStreamResponse chunk)
    {
        await Task.Yield();
        yield return chunk;
    }

    private static string Render(FeedView feed)
    {
        var builder = new DL.DisplayListBuilder();
        feed.Render(new Andy.Tui.Layout.Rect(0, 0, 100, 50), new DL.DisplayListBuilder().Build(), builder);
        return string.Join("\n", builder.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content));
    }
}
