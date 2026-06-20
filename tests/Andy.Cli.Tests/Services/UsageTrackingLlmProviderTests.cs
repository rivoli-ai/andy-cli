using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Model.Llm;
using Andy.Model.Model;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class UsageTrackingLlmProviderTests
{
    private static LlmRequest Req() => new() { Messages = Array.Empty<Message>() };

    private static LlmResponse ResponseWith(LlmUsage? usage) => new()
    {
        AssistantMessage = new Message { Role = Role.Assistant, Content = "hi" },
        Usage = usage
    };

    private static LlmResponse ResponseWith(string content, bool withToolCalls)
    {
        var msg = new Message
        {
            Role = Role.Assistant,
            Content = content,
            ToolCalls = withToolCalls
                ? new List<ToolCall> { new ToolCall { Id = "call_1", Name = "read_file", ArgumentsJson = "{}" } }
                : new List<ToolCall>()
        };
        return new LlmResponse { AssistantMessage = msg };
    }

    private sealed class FakeProvider : ILlmProvider
    {
        private readonly LlmResponse _response;
        private readonly LlmStreamResponse[] _chunks;
        public int CompleteCalls { get; private set; }

        public FakeProvider(LlmResponse response, params LlmStreamResponse[] chunks)
        {
            _response = response;
            _chunks = chunks;
        }

        public string Name => "fake";
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult(Enumerable.Empty<ModelInfo>());

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            CompleteCalls++;
            return Task.FromResult(_response);
        }

        public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var c in _chunks)
            {
                await Task.Yield();
                yield return c;
            }
        }
    }

    [Fact]
    public async Task CompleteAsync_ReportsRealUsage_AndReturnsResponse()
    {
        var captured = new List<LlmUsage>();
        var inner = new FakeProvider(ResponseWith(new LlmUsage { PromptTokens = 100, CompletionTokens = 20, TotalTokens = 120 }));
        var sut = new UsageTrackingLlmProvider(inner, captured.Add);

        var response = await sut.CompleteAsync(Req());

        Assert.Equal("hi", response.Content);
        var usage = Assert.Single(captured);
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(20, usage.CompletionTokens);
    }

    [Fact]
    public async Task CompleteAsync_NoUsage_DoesNotReport()
    {
        var captured = new List<LlmUsage>();
        var inner = new FakeProvider(ResponseWith(null));
        var sut = new UsageTrackingLlmProvider(inner, captured.Add);

        await sut.CompleteAsync(Req());

        Assert.Empty(captured);
    }

    [Fact]
    public async Task StreamCompleteAsync_ReportsUsageForChunksThatHaveIt_AndYieldsAll()
    {
        var captured = new List<LlmUsage>();
        var inner = new FakeProvider(
            ResponseWith(null),
            new LlmStreamResponse { Delta = new Message { Role = Role.Assistant, Content = "a" } },
            new LlmStreamResponse { Usage = new LlmUsage { PromptTokens = 50, CompletionTokens = 5 }, IsComplete = true });
        var sut = new UsageTrackingLlmProvider(inner, captured.Add);

        var chunks = new List<LlmStreamResponse>();
        await foreach (var c in sut.StreamCompleteAsync(Req()))
            chunks.Add(c);

        Assert.Equal(2, chunks.Count);
        var usage = Assert.Single(captured);
        Assert.Equal(50, usage.PromptTokens);
        Assert.Equal(5, usage.CompletionTokens);
    }

    [Fact]
    public async Task CallbackException_DoesNotPropagate()
    {
        var inner = new FakeProvider(ResponseWith(new LlmUsage { PromptTokens = 1, CompletionTokens = 1 }));
        var sut = new UsageTrackingLlmProvider(inner, _ => throw new InvalidOperationException("boom"));

        // Should complete normally despite the throwing callback.
        var response = await sut.CompleteAsync(Req());
        Assert.Equal("hi", response.Content);
    }

    [Fact]
    public async Task CompleteAsync_WithToolCalls_ReportsIntermediateText()
    {
        var captured = new List<string>();
        var inner = new FakeProvider(ResponseWith("I'll first read the file...", withToolCalls: true));
        var sut = new UsageTrackingLlmProvider(inner, _ => { }, captured.Add);

        await sut.CompleteAsync(Req());

        var text = Assert.Single(captured);
        Assert.Equal("I'll first read the file...", text);
    }

    [Fact]
    public async Task CompleteAsync_NoToolCalls_DoesNotReportIntermediateText()
    {
        // A response with no tool calls is the final answer; it is rendered by the end-of-turn
        // path, so it must NOT be surfaced as intermediate text (would double-render it).
        var captured = new List<string>();
        var inner = new FakeProvider(ResponseWith("Here is the final answer.", withToolCalls: false));
        var sut = new UsageTrackingLlmProvider(inner, _ => { }, captured.Add);

        await sut.CompleteAsync(Req());

        Assert.Empty(captured);
    }

    [Fact]
    public async Task CompleteAsync_EmptyContentWithToolCalls_DoesNotReportIntermediateText()
    {
        var captured = new List<string>();
        var inner = new FakeProvider(ResponseWith("   ", withToolCalls: true));
        var sut = new UsageTrackingLlmProvider(inner, _ => { }, captured.Add);

        await sut.CompleteAsync(Req());

        Assert.Empty(captured);
    }

    [Fact]
    public async Task IntermediateTextCallbackException_DoesNotPropagate()
    {
        var inner = new FakeProvider(ResponseWith("narration", withToolCalls: true));
        var sut = new UsageTrackingLlmProvider(
            inner, _ => { }, _ => throw new InvalidOperationException("boom"));

        // Should complete normally despite the throwing intermediate-text callback.
        var response = await sut.CompleteAsync(Req());
        Assert.Equal("narration", response.Content);
    }

    [Fact]
    public async Task NoIntermediateCallback_DoesNotThrow_WithToolCalls()
    {
        // The legacy 3-arg constructor leaves the intermediate-text callback null.
        var inner = new FakeProvider(ResponseWith("narration", withToolCalls: true));
        var sut = new UsageTrackingLlmProvider(inner, _ => { });

        var response = await sut.CompleteAsync(Req());
        Assert.Equal("narration", response.Content);
    }

    [Fact]
    public async Task DelegatesNameAndAvailabilityAndModels()
    {
        var inner = new FakeProvider(ResponseWith(null));
        var sut = new UsageTrackingLlmProvider(inner, _ => { });

        Assert.Equal("fake", sut.Name);
        Assert.True(await sut.IsAvailableAsync());
        Assert.Empty(await sut.ListModelsAsync());
    }
}
