using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.Cli.Instrumentation;

/// <summary>
/// Central hub for collecting and publishing instrumentation events.
/// Provides real-time access to all Engine/LLM activity.
/// </summary>
public class InstrumentationHub
{
    private static readonly Lazy<InstrumentationHub> _instance = new(() => new InstrumentationHub());
    public static InstrumentationHub Instance => _instance.Value;

    private readonly ConcurrentQueue<InstrumentationEvent> _eventQueue = new();
    private readonly ConcurrentDictionary<Guid, Func<InstrumentationEvent, Task>> _subscribers = new();

    /// <summary>
    /// Maximum number of events retained in the in-memory history buffer.
    ///
    /// Retention policy: the history is a bounded FIFO ring. Once the buffer reaches
    /// this size, the oldest events are dropped as new ones arrive (see
    /// <see cref="Publish"/>). This caps memory growth for long-running sessions and
    /// bounds how much historical activity a newly connected client can replay.
    /// </summary>
    public const int MaxEventHistory = 1000;

    private string? _systemPrompt;

    private InstrumentationHub() { }

    /// <summary>
    /// Set the system prompt for the current session
    /// </summary>
    public void SetSystemPrompt(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
    }

    /// <summary>
    /// Get the system prompt for the current session
    /// </summary>
    public string? GetSystemPrompt()
    {
        return _systemPrompt;
    }

    /// <summary>
    /// Publish an event to all subscribers
    /// </summary>
    public void Publish(InstrumentationEvent evt)
    {
        _eventQueue.Enqueue(evt);

        // Trim old events to prevent memory growth
        while (_eventQueue.Count > MaxEventHistory)
        {
            _eventQueue.TryDequeue(out _);
        }

        // Notify all subscribers (fire and forget)
        foreach (var subscriber in _subscribers.Values)
        {
            _ = Task.Run(() => subscriber(evt));
        }
    }

    /// <summary>
    /// Subscribe to events. Dispose the returned handle to unsubscribe, which is
    /// required so the instrumentation server can deterministically release its
    /// subscription on shutdown.
    /// </summary>
    public IDisposable Subscribe(Func<InstrumentationEvent, Task> handler)
    {
        var key = Guid.NewGuid();
        _subscribers[key] = handler;
        return new Subscription(() => _subscribers.TryRemove(key, out _));
    }

    /// <summary>
    /// Number of active subscribers. Exposed for diagnostics and testing.
    /// </summary>
    public int SubscriberCount => _subscribers.Count;

    /// <summary>
    /// Get all events in the current session
    /// </summary>
    public IEnumerable<InstrumentationEvent> GetEventHistory()
    {
        return _eventQueue.ToArray();
    }

    /// <summary>
    /// Clear event history
    /// </summary>
    public void Clear()
    {
        _eventQueue.Clear();
    }

    /// <summary>
    /// Serialize an event to JSON
    /// </summary>
    public string SerializeEvent(InstrumentationEvent evt)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        return JsonSerializer.Serialize(evt, evt.GetType(), options);
    }

    private class Subscription : IDisposable
    {
        private readonly Action _onDispose;

        public Subscription(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose() => _onDispose();
    }
}
