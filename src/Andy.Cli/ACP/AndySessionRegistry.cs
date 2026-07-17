using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.ACP;

/// <summary>
/// Represents a single ACP session and its owned resources (the backing
/// engine agent and the cancellation source for the in-flight prompt).
/// Implements an explicit create/access/cancel/dispose lifecycle so sessions
/// are never retained indefinitely.
/// </summary>
public sealed class AcpSessionEntry : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCts;

    public string SessionId { get; }

    /// <summary>
    /// The backing engine agent. May be null in tests that exercise lifecycle
    /// behavior without a real engine.
    /// </summary>
    public ISessionAgent? Agent { get; }

    public DateTime CreatedAt { get; }
    public DateTime LastAccessedAt { get; private set; }
    public int MessageCount { get; private set; }
    public string Mode { get; internal set; }
    public string Model { get; internal set; }
    public bool IsDisposed { get; private set; }

    public AcpSessionEntry(string sessionId, ISessionAgent? agent, string mode, string model)
    {
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        Agent = agent;
        Mode = mode;
        Model = model;
        CreatedAt = DateTime.UtcNow;
        LastAccessedAt = CreatedAt;
    }

    /// <summary>Updates the last-accessed timestamp used for LRU eviction.</summary>
    public void Touch() => LastAccessedAt = DateTime.UtcNow;

    public void IncrementMessageCount()
    {
        lock (_gate)
        {
            MessageCount++;
        }
    }

    /// <summary>
    /// Marks the start of a prompt. Returns a token linked to the supplied
    /// external token so that either the transport OR an explicit ACP cancel
    /// request reaches the active engine operation.
    /// </summary>
    public CancellationToken BeginPrompt(CancellationToken external)
    {
        lock (_gate)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(AcpSessionEntry));
            }

            _activeCts?.Dispose();
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(external);
            Touch();
            return _activeCts.Token;
        }
    }

    /// <summary>Marks the end of a prompt and releases the linked source.</summary>
    public void EndPrompt()
    {
        lock (_gate)
        {
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    /// <summary>
    /// Cancels the in-flight prompt, if any. Returns true when there was an
    /// active operation to cancel.
    /// </summary>
    public bool CancelActivePrompt()
    {
        lock (_gate)
        {
            if (_activeCts != null && !_activeCts.IsCancellationRequested)
            {
                _activeCts.Cancel();
                return true;
            }

            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            try
            {
                _activeCts?.Cancel();
            }
            catch
            {
                // Best-effort cancellation during teardown.
            }

            _activeCts?.Dispose();
            _activeCts = null;
            Agent?.Dispose();
        }
    }
}

/// <summary>
/// Thread-safe registry that owns ACP session entries with bounded retention.
/// When the configured cap is reached the least-recently-accessed session is
/// evicted and disposed, preventing unbounded growth of engine agents.
/// </summary>
public sealed class AndySessionRegistry : IDisposable
{
    /// <summary>Default maximum number of concurrently retained sessions.</summary>
    public const int DefaultMaxSessions = 50;

    private readonly int _maxSessions;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, AcpSessionEntry> _sessions = new();
    private bool _disposed;

    public AndySessionRegistry(int maxSessions = DefaultMaxSessions, ILogger? logger = null)
    {
        if (maxSessions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSessions), "At least one session must be retained.");
        }

        _maxSessions = maxSessions;
        _logger = logger;
    }

    public int MaxSessions => _maxSessions;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// Adds a session, evicting and disposing the least-recently-used entry
    /// first if the cap would be exceeded.
    /// </summary>
    public void Add(AcpSessionEntry entry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        AcpSessionEntry? evicted = null;
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AndySessionRegistry));
            }

            if (!_sessions.ContainsKey(entry.SessionId) && _sessions.Count >= _maxSessions)
            {
                var lru = _sessions.Values.OrderBy(s => s.LastAccessedAt).First();
                _sessions.Remove(lru.SessionId);
                evicted = lru;
            }

            _sessions[entry.SessionId] = entry;
        }

        if (evicted != null)
        {
            _logger?.LogInformation(
                "Evicted least-recently-used ACP session {SessionId} (cap {Cap} reached)",
                evicted.SessionId, _maxSessions);
            evicted.Dispose();
        }
    }

    /// <summary>Gets a session and refreshes its last-accessed timestamp.</summary>
    public bool TryGet(string sessionId, out AcpSessionEntry entry)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var found))
            {
                found.Touch();
                entry = found;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>Removes and disposes a session. Returns true if it existed.</summary>
    public bool Remove(string sessionId)
    {
        AcpSessionEntry? removed = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var found))
            {
                _sessions.Remove(sessionId);
                removed = found;
            }
        }

        if (removed != null)
        {
            removed.Dispose();
            return true;
        }

        return false;
    }

    public IReadOnlyCollection<string> SessionIds
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Keys.ToArray();
            }
        }
    }

    public void Dispose()
    {
        List<AcpSessionEntry> toDispose;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toDispose = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var entry in toDispose)
        {
            entry.Dispose();
        }
    }
}
