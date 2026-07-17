using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.ContentPipeline;
using Xunit;

namespace Andy.Cli.Tests.Services.ContentPipeline;

/// <summary>
/// Stress + determinism tests for #178: exactly-once, in-order rendering under concurrent
/// arrival and finalization; deterministic (no arbitrary delay) finalization; and
/// non-blocking async disposal.
/// </summary>
public class ContentPipelineCompletionTests
{
    private sealed class RecordingRenderer : IContentRenderer
    {
        private readonly object _gate = new();
        public List<string> RenderedIds { get; } = new();

        public void Render(IContentBlock block)
        {
            lock (_gate)
            {
                RenderedIds.Add(block.Id);
            }
        }
    }

    private static Andy.Cli.Services.ContentPipeline.ContentPipeline CreatePipeline(RecordingRenderer renderer)
    {
        return new Andy.Cli.Services.ContentPipeline.ContentPipeline(
            new MarkdownContentProcessor(),
            new TextContentSanitizer(),
            renderer);
    }

    [Fact]
    public async Task Finalization_Is_Deterministic_No_Blocks_Missing()
    {
        // Determinism: right after FinalizeAsync returns (no sleeps anywhere), every enqueued
        // block must already be rendered.
        var renderer = new RecordingRenderer();
        await using var pipeline = CreatePipeline(renderer);

        const int count = 200;
        for (var i = 0; i < count; i++)
        {
            pipeline.AddBlock(new TextBlock($"b_{i:D4}", $"content {i}"));
        }

        await pipeline.FinalizeAsync();

        Assert.Equal(count, renderer.RenderedIds.Count);
    }

    [Fact]
    public async Task Blocks_Are_Rendered_Exactly_Once_And_In_Order()
    {
        var renderer = new RecordingRenderer();
        await using var pipeline = CreatePipeline(renderer);

        const int count = 500;
        var expected = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var id = $"b_{i:D4}";
            expected.Add(id);
            pipeline.AddBlock(new TextBlock(id, $"content {i}"));
        }

        await pipeline.FinalizeAsync();

        // Exactly once: no duplicates, correct count.
        Assert.Equal(count, renderer.RenderedIds.Count);
        Assert.Equal(count, renderer.RenderedIds.Distinct().Count());
        // In order: rendered order equals arrival order.
        Assert.Equal(expected, renderer.RenderedIds);
    }

    [Fact]
    public async Task Concurrent_Producers_Each_Block_Rendered_Exactly_Once()
    {
        var renderer = new RecordingRenderer();
        await using var pipeline = CreatePipeline(renderer);

        const int producers = 8;
        const int perProducer = 250;

        var tasks = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var k = 0; k < perProducer; k++)
            {
                pipeline.AddBlock(new TextBlock($"p{p}_k{k:D4}", $"content {p}/{k}"));
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        await pipeline.FinalizeAsync();

        var total = producers * perProducer;
        Assert.Equal(total, renderer.RenderedIds.Count);
        Assert.Equal(total, renderer.RenderedIds.Distinct().Count());

        // Per-producer arrival order is preserved even though producers interleave globally.
        foreach (var p in Enumerable.Range(0, producers))
        {
            var indices = renderer.RenderedIds
                .Where(id => id.StartsWith($"p{p}_", StringComparison.Ordinal))
                .Select(id => int.Parse(id.Substring(id.IndexOf("_k", StringComparison.Ordinal) + 2)))
                .ToList();

            Assert.Equal(perProducer, indices.Count);
            var sorted = indices.OrderBy(x => x).ToList();
            Assert.Equal(sorted, indices);
        }
    }

    [Fact]
    public async Task Finalization_Racing_Arrival_Never_Duplicates_Or_Reorders()
    {
        // A producer keeps adding while finalization happens concurrently. Whatever gets
        // rendered must be a duplicate-free, strictly in-order prefix (late blocks may be
        // dropped once finalized, but nothing is rendered twice or out of order).
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var renderer = new RecordingRenderer();
            var pipeline = CreatePipeline(renderer);

            const int count = 300;
            var producer = Task.Run(() =>
            {
                for (var i = 0; i < count; i++)
                {
                    pipeline.AddBlock(new TextBlock($"b_{i:D5}", $"content {i}"));
                }
            });

            // Finalize concurrently with the producer.
            var finalizeTask = Task.Run(async () =>
            {
                await Task.Yield();
                await pipeline.FinalizeAsync();
            });

            await Task.WhenAll(producer, finalizeTask);
            await pipeline.DisposeAsync();

            // No duplicates.
            Assert.Equal(renderer.RenderedIds.Count, renderer.RenderedIds.Distinct().Count());

            // Strictly increasing (in-order) sequence numbers.
            var seq = renderer.RenderedIds
                .Select(id => int.Parse(id.Substring(2)))
                .ToList();
            for (var i = 1; i < seq.Count; i++)
            {
                Assert.True(seq[i] > seq[i - 1], $"Out-of-order render: {seq[i - 1]} then {seq[i]}");
            }
        }
    }

    [Fact]
    public async Task Deferred_System_Messages_Render_After_Content_In_Priority_Order()
    {
        var renderer = new RecordingRenderer();
        await using var pipeline = CreatePipeline(renderer);

        // Two content blocks (rendered immediately) interleaved with high-priority system
        // messages (deferred until drain, then flushed in priority order).
        pipeline.AddBlock(new TextBlock("content_a", "A"));
        pipeline.AddSystemMessage("sys high number", SystemMessageType.Context, priority: 2000);
        pipeline.AddBlock(new TextBlock("content_b", "B"));
        pipeline.AddSystemMessage("sys low number", SystemMessageType.Info, priority: 1000);

        await pipeline.FinalizeAsync();

        Assert.Equal(4, renderer.RenderedIds.Count);
        // Content first (arrival order), then deferred system messages by ascending priority.
        Assert.Equal("content_a", renderer.RenderedIds[0]);
        Assert.Equal("content_b", renderer.RenderedIds[1]);
        Assert.StartsWith("system_Info_", renderer.RenderedIds[2]);
        Assert.StartsWith("system_Context_", renderer.RenderedIds[3]);
    }

    [Fact]
    public async Task Finalize_Is_Idempotent()
    {
        var renderer = new RecordingRenderer();
        await using var pipeline = CreatePipeline(renderer);

        pipeline.AddBlock(new TextBlock("only", "content"));

        await pipeline.FinalizeAsync();
        await pipeline.FinalizeAsync();

        Assert.Single(renderer.RenderedIds);
    }

    [Fact]
    public async Task Content_Added_After_Finalization_Is_Ignored()
    {
        var renderer = new RecordingRenderer();
        await using var pipeline = CreatePipeline(renderer);

        pipeline.AddBlock(new TextBlock("first", "content"));
        await pipeline.FinalizeAsync();

        pipeline.AddBlock(new TextBlock("late", "ignored"));

        Assert.Single(renderer.RenderedIds);
        Assert.Equal("first", renderer.RenderedIds[0]);
    }

    [Fact]
    public async Task DisposeAsync_Without_Finalize_Does_Not_Hang_Or_Throw()
    {
        var renderer = new RecordingRenderer();
        var pipeline = CreatePipeline(renderer);
        pipeline.AddBlock(new TextBlock("b", "content"));

        var dispose = pipeline.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(dispose, completed);
        await dispose; // observe any exception
    }

    [Fact]
    public void Dispose_Is_NonBlocking_And_Safe_To_Call_Twice()
    {
        var renderer = new RecordingRenderer();
        var pipeline = CreatePipeline(renderer);
        pipeline.AddBlock(new TextBlock("b", "content"));

        pipeline.Dispose();
        pipeline.Dispose();
    }
}
