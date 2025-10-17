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
    private readonly ConcurrentBag<Func<InstrumentationEvent, Task>> _subscribers = new();
    private readonly int _maxEventHistory = 1000;
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
        while (_eventQueue.Count > _maxEventHistory)
        {
            _eventQueue.TryDequeue(out _);
        }

        // Notify all subscribers (fire and forget)
        foreach (var subscriber in _subscribers)
        {
            _ = Task.Run(() => subscriber(evt));
        }
    }

    /// <summary>
    /// Subscribe to events
    /// </summary>
    public IDisposable Subscribe(Func<InstrumentationEvent, Task> handler)
    {
        _subscribers.Add(handler);
        return new Subscription(() => { /* Removal not implemented for simplicity */ });
    }

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
