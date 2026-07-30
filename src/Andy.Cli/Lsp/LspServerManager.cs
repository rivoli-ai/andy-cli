using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Lsp.Protocol;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Lsp;

/// <summary>Lifecycle state of one configured language server within a workspace.</summary>
public enum LspServerState
{
    /// <summary>Configured but never needed yet. Servers start lazily, on the first matching mutation.</summary>
    NotStarted,

    /// <summary>The initialize handshake is in flight.</summary>
    Starting,

    /// <summary>Initialized and answering.</summary>
    Running,

    /// <summary>Never started. <see cref="LspServerStatus.Detail"/> says why.</summary>
    Failed,

    /// <summary>Started once and then went away.</summary>
    Crashed,

    /// <summary>Turned off by configuration.</summary>
    Disabled,
}

/// <summary>A snapshot of one server, as shown by <c>/lsp status</c>.</summary>
public sealed record LspServerStatus(
    string ServerId,
    LspServerState State,
    string? Root,
    string Command,
    IReadOnlyList<string> Extensions,
    string? Detail = null,
    DateTimeOffset? StartedAt = null,
    int RestartCount = 0,
    int MalformedMessageCount = 0);

/// <summary>
/// Owns every language-server process for one workspace.
///
/// Three properties this type exists to guarantee:
///
/// 1. ONE server per (definition, project root). Concurrent mutations of two files that share a
///    root must not race into two processes, so startup is deduplicated through a single cached
///    task per key - every caller awaits the same handshake.
/// 2. A failure is remembered. A missing binary or a server that refuses to initialize is recorded
///    and NOT retried on every subsequent file write; otherwise a misconfigured server would spawn
///    a process per edit. <c>/lsp restart</c> clears the memory deliberately.
/// 3. Nothing outlives the workspace. Every process is terminated on disposal, including ones
///    still mid-handshake, so a session cannot leave orphans behind.
/// </summary>
public sealed class LspServerManager : IAsyncDisposable
{
    /// <summary>
    /// How many times a crashed server is restarted automatically before it is left down. A server
    /// that dies on every request would otherwise be relaunched once per file write forever.
    /// </summary>
    public const int MaxAutomaticRestarts = 2;

    private readonly string _workspaceRoot;
    private readonly Func<LspServerDefinition, string, ILspTransport> _transportFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ServerEntry> _entries = new(StringComparer.Ordinal);
    private int _disposed;

    public LspServerManager(
        LspConfigurationLoadResult configuration,
        string workspaceRoot,
        Func<LspServerDefinition, string, ILspTransport>? transportFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        Configuration = configuration ?? LspConfigurationLoadResult.Empty;
        _workspaceRoot = System.IO.Path.GetFullPath(workspaceRoot);
        _transportFactory = transportFactory ?? StdioLspTransport.Start;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<LspServerManager>();
    }

    public LspConfigurationLoadResult Configuration { get; }

    public string WorkspaceRoot => _workspaceRoot;

    /// <summary>Definition claiming <paramref name="path"/>, or null when none does.</summary>
    public LspServerDefinition? ResolveDefinition(string path) =>
        Configuration.Servers.FirstOrDefault(definition => definition.Enabled && definition.Matches(path));

    /// <summary>
    /// Returns the running client for a definition and root, starting it if needed. Never throws:
    /// a failure to start is recorded as status and reported as null, because the caller is on the
    /// agent's critical path.
    /// </summary>
    public async Task<LspClient?> GetOrStartAsync(
        LspServerDefinition definition,
        string root,
        CancellationToken cancellationToken)
    {
        if (_disposed != 0) return null;

        var key = MakeKey(definition.Id, root);

        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new ServerEntry(definition, root));

            // A server that died since the last call gets one bounded second chance.
            if (entry.IsStale && entry.RestartCount < MaxAutomaticRestarts)
            {
                if (_entries.TryRemove(new KeyValuePair<string, ServerEntry>(key, entry)))
                {
                    var previousRestarts = entry.RestartCount;
                    await entry.DisposeAsync().ConfigureAwait(false);
                    _entries.TryAdd(key, new ServerEntry(definition, root) { RestartCount = previousRestarts + 1 });
                    continue;
                }
                continue;
            }

            // Deduplicated startup: whichever caller created the entry runs the handshake, everyone
            // else awaits the same task.
            return await entry.GetClientAsync(this, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LspClient?> StartAsync(
        ServerEntry entry,
        CancellationToken cancellationToken)
    {
        ILspTransport? transport = null;
        try
        {
            transport = _transportFactory(entry.Definition, entry.Root);
            var client = await LspClient.StartAsync(
                entry.Definition,
                entry.Root,
                transport,
                _loggerFactory?.CreateLogger<LspClient>(),
                cancellationToken).ConfigureAwait(false);
            entry.MarkRunning(client);
            return client;
        }
        catch (LspStartupException ex)
        {
            entry.MarkFailed(ex.Message);
            _logger?.LogWarning("LSP {Server}: {Detail}", entry.Definition.Id, ex.Message);
            await SafeDisposeAsync(transport).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            entry.MarkFailed("startup cancelled");
            await SafeDisposeAsync(transport).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            entry.MarkFailed($"unexpected startup failure: {ex.Message}");
            _logger?.LogWarning(ex, "LSP {Server}: unexpected startup failure", entry.Definition.Id);
            await SafeDisposeAsync(transport).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task SafeDisposeAsync(ILspTransport? transport)
    {
        if (transport is null) return;
        try
        {
            transport.Terminate();
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup of a server that never came up.
        }
    }

    /// <summary>
    /// Restarts every server, or only those matching <paramref name="serverId"/>. Also clears
    /// remembered failures so a newly installed binary is picked up without restarting Andy.
    /// Returns the number of entries dropped.
    /// </summary>
    public async Task<int> RestartAsync(string? serverId, CancellationToken cancellationToken = default)
    {
        var dropped = 0;
        foreach (var (key, entry) in _entries.ToArray())
        {
            if (serverId is not null &&
                !string.Equals(entry.Definition.Id, serverId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_entries.TryRemove(new KeyValuePair<string, ServerEntry>(key, entry)))
            {
                await entry.DisposeAsync().ConfigureAwait(false);
                dropped++;
            }
        }

        return dropped;
    }

    /// <summary>Current state of every configured server, including ones never started.</summary>
    public IReadOnlyList<LspServerStatus> GetStatuses()
    {
        var statuses = new List<LspServerStatus>();
        var live = _entries.Values.ToList();

        foreach (var definition in Configuration.Servers)
        {
            var matching = live.Where(e =>
                string.Equals(e.Definition.Id, definition.Id, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matching.Count == 0)
            {
                statuses.Add(new LspServerStatus(
                    definition.Id,
                    definition.Enabled ? LspServerState.NotStarted : LspServerState.Disabled,
                    null,
                    FormatCommand(definition),
                    definition.Extensions,
                    definition.Enabled ? "not started yet (servers start on the first matching file change)" : "disabled"));
                continue;
            }

            foreach (var entry in matching)
            {
                statuses.Add(entry.ToStatus(FormatCommand(definition)));
            }
        }

        return statuses;
    }

    private static string FormatCommand(LspServerDefinition definition) =>
        definition.Args.Count == 0
            ? definition.Command
            : definition.Command + " " + string.Join(" ", definition.Args);

    private static string MakeKey(string serverId, string root) =>
        serverId.ToLowerInvariant() + " " + System.IO.Path.GetFullPath(root);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var entry in _entries.Values.ToArray())
        {
            await entry.DisposeAsync().ConfigureAwait(false);
        }
        _entries.Clear();
    }

    /// <summary>
    /// One (definition, root) pair. Holds the single shared startup task that makes concurrent
    /// callers converge on one process.
    /// </summary>
    private sealed class ServerEntry : IAsyncDisposable
    {
        private readonly object _sync = new();
        private Task<LspClient?>? _startup;
        private LspClient? _client;
        private int _disposed;

        public ServerEntry(LspServerDefinition definition, string root)
        {
            Definition = definition;
            Root = root;
        }

        public LspServerDefinition Definition { get; }
        public string Root { get; }
        public int RestartCount { get; init; }
        public LspServerState State { get; private set; } = LspServerState.NotStarted;
        public string? Detail { get; private set; }
        public DateTimeOffset? StartedAt { get; private set; }

        /// <summary>The server started and has since gone away, so a restart could help.</summary>
        public bool IsStale
        {
            get
            {
                lock (_sync)
                {
                    if (_disposed != 0) return false;
                    if (_client is null) return false;
                    if (_client.IsAlive) return false;
                    State = LspServerState.Crashed;
                    Detail ??= "server exited";
                    return true;
                }
            }
        }

        public Task<LspClient?> GetClientAsync(LspServerManager manager, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_disposed != 0) return Task.FromResult<LspClient?>(null);
                if (_startup is not null) return _startup;

                State = LspServerState.Starting;

                // Deliberately NOT flowed the caller's cancellation token into the shared startup:
                // one turn being cancelled must not tear down a handshake other callers are awaiting.
                // Callers observe their own cancellation through their own await.
                _startup = manager.StartAsync(this, CancellationToken.None);
                return _startup;
            }
        }

        public void MarkRunning(LspClient client)
        {
            lock (_sync)
            {
                _client = client;
                State = LspServerState.Running;
                Detail = null;
                StartedAt = client.StartedAt;
            }
        }

        public void MarkFailed(string detail)
        {
            lock (_sync)
            {
                State = LspServerState.Failed;
                Detail = detail;
            }
        }

        public LspServerStatus ToStatus(string command)
        {
            lock (_sync)
            {
                var state = State;
                if (state == LspServerState.Running && _client is not null && !_client.IsAlive)
                {
                    state = LspServerState.Crashed;
                }

                var detail = Detail;
                if (state == LspServerState.Crashed && _client is not null)
                {
                    var stderr = _client.StandardErrorTail;
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        detail = (detail is null ? string.Empty : detail + " ") + "stderr: " + stderr;
                    }
                }

                return new LspServerStatus(
                    Definition.Id,
                    state,
                    Root,
                    command,
                    Definition.Extensions,
                    detail,
                    StartedAt,
                    RestartCount,
                    _client?.MalformedMessageCount ?? 0);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task<LspClient?>? startup;
            lock (_sync)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                startup = _startup;
            }

            // Await the in-flight handshake so a server started microseconds before shutdown is
            // still owned by someone who will kill it. Bounded, so a hung server cannot block exit.
            if (startup is not null)
            {
                try
                {
                    var client = await startup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Startup failed or timed out; StartAsync already released its transport.
                }
            }
        }
    }
}
