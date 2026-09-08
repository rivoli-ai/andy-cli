using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Moq;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Services;

public class PendingMessageHandoffTests
{
    [Fact]
    public async Task RevisedInputSteersCurrentTurnAfterToolRoundAndRemovedInputIsNotSent()
    {
        var queue = new PendingMessageQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Mock<IToolExecutor>();
        executor.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<ToolExecutionContext>()))
            .Returns<string, Dictionary<string, object?>, ToolExecutionContext>(async (_, _, context) =>
            {
                started.TrySetResult();
                await finish.Task.WaitAsync(context.CancellationToken);
                return new ToolExecutionResult { IsSuccessful = true, Data = "tool finished" };
            });
        var requests = new List<LlmRequest>();
        var provider = new Mock<ILlmProvider>();
        provider.SetupGet(p => p.Name).Returns("stub");
        provider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LlmRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(requests.Count == 1
                    ? new LlmResponse { AssistantMessage = new Message { Role = Role.Assistant, ToolCalls = new List<ToolCall> { new() { Id = "call", Name = "work", ArgumentsJson = "{}" } } } }
                    : new LlmResponse { AssistantMessage = new Message { Role = Role.Assistant, Content = "done" } });
            });
        var registry = new Mock<IToolRegistry>();
        registry.SetupGet(r => r.Tools).Returns(Array.Empty<ToolRegistration>());
        registry.Setup(r => r.GetTools()).Returns(Array.Empty<ToolRegistration>());
        using var service = new SimpleAssistantService(provider.Object, registry.Object, executor.Object, new FeedView(), "model", "stub");
        var accepted = new List<long>();
        var delivery = new PendingMessageDelivery(queue.Drain, queue.RestoreFront,
            (message, _) => Task.FromResult<IReadOnlyList<MessagePart>>(new MessagePart[] { new TextPart(message.Text) }),
            batch => accepted.AddRange(batch.Select(m => m.Id)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = service.ProcessMessageAsync("initial", cancellationToken: deadline.Token, pendingInputProvider: delivery.TakeAsync);
        await started.Task.WaitAsync(deadline.Token);
        var revise = queue.Enqueue("old draft", 2);
        var remove = queue.Enqueue("discard this", 3);
        Assert.True(queue.TryUpdate(revise.Id, "new direction", out _));
        Assert.True(queue.TryRemove(remove.Id, out _));
        Assert.Empty(accepted);
        finish.SetResult();
        Assert.Equal("done", await run);
        Assert.Equal(new[] { revise.Id }, accepted);
        Assert.Empty(queue.Snapshot());
        Assert.Contains(requests[1].Messages, m => m.Role == Role.User && m.Content == "new direction");
        Assert.DoesNotContain(requests[1].Messages, m => m.Content is "old draft" or "discard this");
        Assert.Single(service.ExportTranscript().Turns);
        Assert.Equal("done", await service.ProcessMessageAsync("later"));
        Assert.Contains(requests[2].Messages, m => m.Content == "new direction");
    }

    [Fact]
    public async Task CancelingPreparationRestoresOriginalOrderAheadOfNewArrivals()
    {
        var queue = new PendingMessageQueue();
        var first = queue.Enqueue("first", 1);
        var second = queue.Enqueue("second", 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        bool accepted = false;
        var delivery = new PendingMessageDelivery(queue.Drain, queue.RestoreFront, async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<MessagePart>();
        }, _ => accepted = true);
        var take = delivery.TakeAsync(cancellation.Token);
        await started.Task;
        var third = queue.Enqueue("third", 3);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => take);
        Assert.False(accepted);
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, queue.Snapshot().Select(m => m.Id));
    }

    [Fact]
    public async Task ArrivalDuringPreparationWaitsForTheNextBoundary()
    {
        var queue = new PendingMessageQueue();
        queue.Enqueue("first", 1);
        var delivery = new PendingMessageDelivery(queue.Drain, queue.RestoreFront, (message, _) =>
        {
            if (message.Text == "first") queue.Enqueue("later", 2);
            return Task.FromResult<IReadOnlyList<MessagePart>>(new MessagePart[] { new TextPart(message.Text) });
        }, _ => { });
        var first = await delivery.TakeAsync(default);
        Assert.Equal("first", Assert.IsType<TextPart>(Assert.Single(Assert.Single(first))).Text);
        Assert.Equal("later", Assert.Single(queue.Snapshot()).Text);
        var second = await delivery.TakeAsync(default);
        Assert.Equal("later", Assert.IsType<TextPart>(Assert.Single(Assert.Single(second))).Text);
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public async Task RemovalAndBoundaryHaveExactlyOneWinner()
    {
        for (int i = 0; i < 1000; i++)
        {
            var queue = new PendingMessageQueue();
            var message = queue.Enqueue("draft", 1);
            bool removed = false;
            IReadOnlyList<PendingUserMessage>? sent = null;
            await Task.WhenAll(Task.Run(() => removed = queue.TryRemove(message.Id, out _)), Task.Run(() => sent = queue.Drain()));
            Assert.Equal(1, (removed ? 1 : 0) + sent!.Count);
            Assert.Empty(queue.Snapshot());
        }
    }

    [Fact]
    public void RecalledBubbleClearlyShowsItWasRemoved()
    {
        var bubble = new UserBubbleItem("draft", 2, UserMessageQueueState.Queued);
        bubble.SetQueueState(UserMessageQueueState.Removed);
        var builder = new DL.DisplayListBuilder();
        bubble.RenderSlice(0, 0, 80, 0, bubble.MeasureLineCount(80), new DL.DisplayListBuilder().Build(), builder);
        Assert.Contains("removed", string.Concat(builder.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content)));
    }
}
