namespace Andy.Cli.Services;

public sealed record PendingUserMessage(long Id, string Text, int MessageNumber, Andy.Cli.Domain.ImageAttachment? Image = null);

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

    public PendingUserMessage Enqueue(string text, int messageNumber, Andy.Cli.Domain.ImageAttachment? image = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var message = new PendingUserMessage(
            Interlocked.Increment(ref _nextId),
            text,
            messageNumber, image);
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

    /// <summary>Atomically remove a message for editing or discarding.</summary>
    public bool TryRemove(long id, out PendingUserMessage message)
    {
        lock (_lock)
        {
            for (var node = _messages.First; node != null; node = node.Next)
            {
                if (node.Value.Id != id) continue;
                message = node.Value;
                _messages.Remove(node);
                return true;
            }
        }
        message = default!;
        return false;
    }

    /// <summary>Take one bounded FIFO snapshot at the tool-round handoff.</summary>
    public IReadOnlyList<PendingUserMessage> Drain()
    {
        lock (_lock)
        {
            var messages = _messages.ToArray();
            _messages.Clear();
            return messages;
        }
    }

    /// <summary>Restore an unprepared snapshot ahead of messages submitted meanwhile.</summary>
    public void RestoreFront(IReadOnlyList<PendingUserMessage> messages)
    {
        lock (_lock)
            for (int i = messages.Count - 1; i >= 0; i--) _messages.AddFirst(messages[i]);
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
