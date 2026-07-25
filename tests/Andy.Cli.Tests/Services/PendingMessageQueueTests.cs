using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class PendingMessageQueueTests
{
    [Fact]
    public void MessagesDequeueInSubmissionOrder()
    {
        var queue = new PendingMessageQueue();
        var first = queue.Enqueue("first", 2);
        var second = queue.Enqueue("second", 3);

        Assert.True(queue.TryDequeue(out var actualFirst));
        Assert.True(queue.TryDequeue(out var actualSecond));
        Assert.Equal(first, actualFirst);
        Assert.Equal(second, actualSecond);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void QueuedMessageCanBeRevisedWithoutChangingIdentityOrOrder()
    {
        var queue = new PendingMessageQueue();
        var first = queue.Enqueue("draft", 2);
        var second = queue.Enqueue("later", 3);

        Assert.True(queue.TryUpdate(first.Id, "revised", out var revised));
        Assert.Equal(first.Id, revised.Id);
        Assert.Equal("revised", revised.Text);
        Assert.Equal(new[] { revised.Id, second.Id }, queue.Snapshot().Select(x => x.Id));
    }

    [Fact]
    public void DequeuedMessageCanNoLongerBeEditedAsPending()
    {
        var queue = new PendingMessageQueue();
        var message = queue.Enqueue("draft", 2);
        Assert.True(queue.TryDequeue(out _));

        Assert.False(queue.TryUpdate(message.Id, "too late", out _));
        Assert.False(queue.Contains(message.Id));
    }
}
