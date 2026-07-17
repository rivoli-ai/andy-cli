using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.ACP;
using Xunit;

namespace Andy.Cli.Tests.ACP;

/// <summary>
/// Tests for the ACP session lifecycle manager: bounded retention, LRU
/// eviction, disposal, and per-session cancellation.
/// </summary>
public class AndySessionRegistryTests
{
    private static AcpSessionEntry NewEntry(string id) => new(id, agent: null, mode: "assistant", model: "andy-cli");

    [Fact]
    public void Add_And_TryGet_RoundTrips()
    {
        using var registry = new AndySessionRegistry();
        var entry = NewEntry("s1");

        registry.Add(entry);

        Assert.True(registry.TryGet("s1", out var found));
        Assert.Same(entry, found);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnknownSession()
    {
        using var registry = new AndySessionRegistry();
        Assert.False(registry.TryGet("missing", out _));
    }

    [Fact]
    public void Constructor_Rejects_NonPositiveCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AndySessionRegistry(maxSessions: 0));
    }

    [Fact]
    public void Add_EvictsAndDisposes_LeastRecentlyUsed_WhenCapExceeded()
    {
        using var registry = new AndySessionRegistry(maxSessions: 2);
        var a = NewEntry("a");
        var b = NewEntry("b");

        registry.Add(a);
        Thread.Sleep(5);
        registry.Add(b);

        // Touch "b" so "a" becomes the least-recently-used.
        Thread.Sleep(5);
        Assert.True(registry.TryGet("b", out _));

        var c = NewEntry("c");
        registry.Add(c);

        Assert.Equal(2, registry.Count);
        Assert.False(registry.TryGet("a", out _));
        Assert.True(a.IsDisposed);
        Assert.True(registry.TryGet("b", out _));
        Assert.True(registry.TryGet("c", out _));
    }

    [Fact]
    public void Add_SameId_DoesNotEvict()
    {
        using var registry = new AndySessionRegistry(maxSessions: 1);
        var a = NewEntry("a");
        registry.Add(a);

        var a2 = NewEntry("a");
        registry.Add(a2); // replace same id, must not evict anything else

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet("a", out var found));
        Assert.Same(a2, found);
    }

    [Fact]
    public void Add_SameId_DisposesDisplacedEntry()
    {
        // Fix 2: replacing an existing id must not leak the old entry (its
        // engine agent + cancellation source would otherwise be orphaned).
        using var registry = new AndySessionRegistry(maxSessions: 5);
        var a = NewEntry("a");
        registry.Add(a);

        var a2 = NewEntry("a");
        registry.Add(a2);

        Assert.True(a.IsDisposed);
        Assert.False(a2.IsDisposed);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet("a", out var found));
        Assert.Same(a2, found);
    }

    [Fact]
    public void Add_SameId_Rejects_WhenExistingHasInFlightPrompt()
    {
        // Fix 2 + Fix 1: a busy entry must never be torn down by a same-id
        // replacement; the duplicate add is rejected instead.
        using var registry = new AndySessionRegistry(maxSessions: 5);
        var busy = NewEntry("a");
        registry.Add(busy);
        busy.BeginPrompt(CancellationToken.None); // mark in-flight

        var replacement = NewEntry("a");
        Assert.Throws<InvalidOperationException>(() => registry.Add(replacement));

        Assert.False(busy.IsDisposed);
        Assert.True(registry.TryGet("a", out var found));
        Assert.Same(busy, found);
    }

    [Fact]
    public void Add_DoesNotEvict_BusySession_WhenCapExceeded()
    {
        // Fix 1: a session with an in-flight prompt is excluded from LRU
        // eviction even when it is the least-recently-accessed entry.
        using var registry = new AndySessionRegistry(maxSessions: 2);
        var busy = NewEntry("busy");
        registry.Add(busy);
        busy.BeginPrompt(CancellationToken.None); // in-flight; do not end

        Thread.Sleep(5);
        var idle = NewEntry("idle");
        registry.Add(idle);

        // "busy" is the oldest but is in-flight, so adding a third session must
        // evict the IDLE one and leave the busy session intact.
        Thread.Sleep(5);
        var third = NewEntry("third");
        registry.Add(third);

        Assert.False(busy.IsDisposed);
        Assert.True(registry.TryGet("busy", out _));
        Assert.True(idle.IsDisposed);
        Assert.False(registry.TryGet("idle", out _));
        Assert.True(registry.TryGet("third", out _));
    }

    [Fact]
    public void Add_AcceptsSoftOverCap_WhenAllSessionsBusy()
    {
        // Fix 1: when every retained session is in-flight, the registry must
        // accept a soft over-cap rather than disposing an active session.
        using var registry = new AndySessionRegistry(maxSessions: 1);
        var busy = NewEntry("busy");
        registry.Add(busy);
        busy.BeginPrompt(CancellationToken.None); // in-flight

        var second = NewEntry("second");
        registry.Add(second); // cap is 1 but busy cannot be evicted

        Assert.False(busy.IsDisposed);
        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGet("busy", out _));
        Assert.True(registry.TryGet("second", out _));
    }

    [Fact]
    public void Remove_DisposesEntry()
    {
        using var registry = new AndySessionRegistry();
        var entry = NewEntry("s1");
        registry.Add(entry);

        Assert.True(registry.Remove("s1"));
        Assert.True(entry.IsDisposed);
        Assert.False(registry.TryGet("s1", out _));
        Assert.False(registry.Remove("s1"));
    }

    [Fact]
    public void Dispose_DisposesAllRetainedSessions()
    {
        var registry = new AndySessionRegistry();
        var a = NewEntry("a");
        var b = NewEntry("b");
        registry.Add(a);
        registry.Add(b);

        registry.Dispose();

        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
    }

    [Fact]
    public void Entry_CancelActivePrompt_CancelsLinkedToken()
    {
        var entry = NewEntry("s1");
        var token = entry.BeginPrompt(CancellationToken.None);

        Assert.False(token.IsCancellationRequested);
        Assert.True(entry.CancelActivePrompt());
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Entry_ExternalCancellation_PropagatesToLinkedToken()
    {
        var entry = NewEntry("s1");
        using var external = new CancellationTokenSource();
        var token = entry.BeginPrompt(external.Token);

        external.Cancel();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Entry_CancelActivePrompt_ReturnsFalse_WhenNoActivePrompt()
    {
        var entry = NewEntry("s1");
        Assert.False(entry.CancelActivePrompt());
    }

    [Fact]
    public void Entry_BeginPrompt_Rejects_SecondConcurrentPrompt()
    {
        // Fix 4: a second concurrent prompt must be rejected rather than
        // disposing the first prompt's still-in-use cancellation source.
        var entry = NewEntry("s1");
        var firstToken = entry.BeginPrompt(CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => entry.BeginPrompt(CancellationToken.None));

        // The first prompt's token remains valid and usable (not disposed).
        Assert.False(firstToken.IsCancellationRequested);
        Assert.True(entry.HasActivePrompt);

        // And it can still be cancelled without an ObjectDisposedException.
        Assert.True(entry.CancelActivePrompt());
        Assert.True(firstToken.IsCancellationRequested);
    }

    [Fact]
    public void Entry_BeginPrompt_AllowedAgain_AfterEndPrompt()
    {
        var entry = NewEntry("s1");
        entry.BeginPrompt(CancellationToken.None);
        entry.EndPrompt();

        Assert.False(entry.HasActivePrompt);
        var token = entry.BeginPrompt(CancellationToken.None); // must not throw
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void Entry_HasActivePrompt_TracksLifecycle()
    {
        var entry = NewEntry("s1");
        Assert.False(entry.HasActivePrompt);

        entry.BeginPrompt(CancellationToken.None);
        Assert.True(entry.HasActivePrompt);

        entry.EndPrompt();
        Assert.False(entry.HasActivePrompt);
    }

    [Fact]
    public void Entry_Dispose_CancelsActivePrompt()
    {
        var entry = NewEntry("s1");
        var token = entry.BeginPrompt(CancellationToken.None);

        entry.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.True(entry.IsDisposed);
    }

    [Fact]
    public async Task Registry_IsolatesConcurrentSessions()
    {
        using var registry = new AndySessionRegistry(maxSessions: 100);

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            var entry = NewEntry($"s{i}");
            registry.Add(entry);
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(20, registry.Count);
        for (int i = 0; i < 20; i++)
        {
            Assert.True(registry.TryGet($"s{i}", out _));
        }
    }
}
