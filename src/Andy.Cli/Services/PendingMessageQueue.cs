namespace Andy.Cli.Services;

public sealed record PendingUserMessage(long Id, string Text, int MessageNumber);

/// <summary>
/// Thread-safe FIFO for user messages submitted while an agent turn is active.
/// Stable ids let the input loop revise a message until the pump dequeues it.
/// </summary>
public sealed class PendingMessageQueue
{
    private readonly object _lock = new();
    private readonly LinkedList<PendingUserMessage> _messages = new();
    private long _nextId;

    public int Count
    {
        get
        {
            lock (_lock) return _messages.Count;
        }
    }

    public PendingUserMessage Enqueue(string text, int messageNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var message = new PendingUserMessage(
            Interlocked.Increment(ref _nextId),
            text,
            messageNumber);
        lock (_lock) _messages.AddLast(message);
        return message;
    }

    public bool TryUpdate(long id, string text, out PendingUserMessage updated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_lock)
        {
            for (var node = _messages.First; node != null; node = node.Next)
            {
                if (node.Value.Id != id) continue;
                updated = node.Value with { Text = text };
                node.Value = updated;
                return true;
            }
        }

        updated = default!;
        return false;
    }

    public bool Contains(long id)
    {
        lock (_lock) return _messages.Any(message => message.Id == id);
    }

    public bool TryDequeue(out PendingUserMessage message)
    {
        lock (_lock)
        {
            if (_messages.First == null)
            {
                message = default!;
                return false;
            }

            message = _messages.First.Value;
            _messages.RemoveFirst();
            return true;
        }
    }

    public IReadOnlyList<PendingUserMessage> Snapshot()
    {
        lock (_lock) return _messages.ToList();
    }
}
