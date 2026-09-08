using System.Runtime.CompilerServices;
using Andy.Acp.Core.Agent;
using Andy.Cli.ACP;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Moq;

namespace Andy.Cli.Tests.ACP;

public class AcpStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealAgentStreamsBeforeCompletionAndHonorsCancellation(bool cancel)
    {
        var provider = new GatedProvider();
        var registry = new Mock<IToolRegistry>();
        registry.SetupGet(r => r.Tools).Returns(Array.Empty<ToolRegistration>());
        using var agent = new SimpleAgentSessionAgentFactory(provider, registry.Object, new Mock<IToolExecutor>().Object, null)
            .Create("Respond.", "stub", "stub");
        var chunks = new List<string>();
        var streamer = new Mock<IResponseStreamer>();
        streamer.Setup(s => s.SendMessageChunkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string text, CancellationToken token) => { token.ThrowIfCancellationRequested(); chunks.Add(text); return Task.CompletedTask; });
        using var cts = new CancellationTokenSource();
        var run = agent.ProcessMessageAsync("Hi", streamer.Object, cts.Token);
        var first = await Task.WhenAny(run, provider.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(10));
        if (first == run) Assert.Fail("Engine ended before streaming: " + (await run).StopReason + " / " + (await run).Response);
        Assert.False(run.IsCompleted);
        Assert.Equal(new[] { "hello " }, chunks);
        if (cancel) cts.Cancel(); else provider.Release.TrySetResult();
        try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) when (cancel) { }
        Assert.Equal(cancel ? new[] { "hello " } : new[] { "hello ", "world" }, chunks);
        Assert.True(agent.StreamsResponses);
    }
    [Fact]
    public async Task NonStreamingProviderFallsBackToOneHonestChunk()
    {
        var provider = new Mock<ILlmProvider>();
        provider.Setup(p => p.StreamCompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new NotSupportedException());
        provider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { AssistantMessage = new Message { Role = Role.Assistant, Content = "whole response" } });
        var registry = new Mock<IToolRegistry>();
        registry.SetupGet(r => r.Tools).Returns(Array.Empty<ToolRegistration>());
        using var agent = new SimpleAgentSessionAgentFactory(provider.Object, registry.Object, new Mock<IToolExecutor>().Object, null)
            .Create("Respond.", "stub", "stub");
        var streamer = new Mock<IResponseStreamer>();
        var result = await agent.ProcessMessageAsync("Hi", streamer.Object, CancellationToken.None);
        Assert.True(result.Success);
        streamer.Verify(s => s.SendMessageChunkAsync("whole response", It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class GatedProvider : ILlmProvider
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "stub";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Must use streaming.");
        public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new LlmStreamResponse { Delta = new Message { Role = Role.Assistant, Content = "hello " } };
            Waiting.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new LlmStreamResponse { Delta = new Message { Role = Role.Assistant, Content = "world" }, FinishReason = "stop", IsComplete = true };
        }
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Empty<ModelInfo>());
    }
}
