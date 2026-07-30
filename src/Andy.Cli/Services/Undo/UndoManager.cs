using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.Undo;

/// <summary>Opaque handle for the turn currently being recorded.</summary>
public sealed class TurnHandle
{
    internal TurnHandle(string prompt, string preSnapshot)
    {
        Prompt = prompt;
        PreSnapshot = preSnapshot;
    }

    /// <summary>The user prompt that started the turn (restored to the composer on undo).</summary>
    public string Prompt { get; }

    /// <summary>Commit id of the pre-turn snapshot.</summary>
    public string PreSnapshot { get; }
}

/// <summary>One completed, reversible turn: the paths it changed plus both endpoints.</summary>
public sealed record UndoTransaction(
    string Prompt,
    string PreSnapshot,
    string PostSnapshot,
    IReadOnlyList<string> ChangedPaths,
    DateTimeOffset CompletedUtc);

/// <summary>Result of an /undo or /redo attempt.</summary>
public sealed record UndoOutcome(
    bool Success,
    string Message,
    string? RestoredPrompt = null,
    IReadOnlyList<string>? ChangedPaths = null);

/// <summary>
/// Tracks each interactive turn as a filesystem transaction backed by shadow Git
/// snapshots and exposes /undo and /redo over that history (issue #276).
///
/// Rules enforced here:
/// - a turn is only undoable once it completes; an interrupted or failed turn
///   records nothing;
/// - starting a new turn invalidates the redo history;
/// - if a turn cannot be snapshotted the whole history is dropped rather than
///   left in a state where an undo would silently skip that turn;
/// - only the paths that actually changed between the two snapshots are restored,
///   so unrelated dirty, untracked and ignored files are never touched;
/// - history is bounded, and the session's snapshots are pruned on dispose.
/// </summary>
public sealed class UndoManager : IDisposable
{
    public const int DefaultMaxTransactions = 20;

    public const string NotGitWorkspaceReason =
        "Undo is unavailable: this workspace is not a Git repository. " +
        "The first release snapshots changes with Git, so run `git init` in the " +
        "workspace to enable /undo and /redo.";

    private readonly object _lock = new();
    private readonly List<UndoTransaction> _undo = new();
    private readonly List<UndoTransaction> _redo = new();
    private readonly ShadowGitRepository? _repository;
    private readonly string _refName;
    private readonly int _maxTransactions;
    private readonly ILogger? _logger;
    private readonly TimeProvider _clock;
    private TurnHandle? _activeTurn;
    private bool _disposed;

    private UndoManager(
        ShadowGitRepository? repository,
        string refName,
        string? unavailableReason,
        int maxTransactions,
        ILogger? logger,
        TimeProvider clock)
    {
        _repository = repository;
        _refName = refName;
        UnavailableReason = unavailableReason;
        _maxTransactions = Math.Max(1, maxTransactions);
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Builds a manager for the workspace. A non-Git workspace (or an unusable
    /// snapshot store) yields a disabled manager that explains itself instead of
    /// throwing, so the CLI keeps working exactly as before.
    /// </summary>
    public static UndoManager Create(
        string workspacePath,
        string sessionId,
        string? snapshotRoot = null,
        int maxTransactions = DefaultMaxTransactions,
        ILogger? logger = null,
        TimeProvider? clock = null)
    {
        var refName = ShadowGitRepository.RefForSession(sessionId);
        var timeProvider = clock ?? TimeProvider.System;

        if (!ShadowGitRepository.IsGitWorkspace(workspacePath))
        {
            return new UndoManager(null, refName, NotGitWorkspaceReason, maxTransactions, logger, timeProvider);
        }

        try
        {
            var repository = new ShadowGitRepository(workspacePath, snapshotRoot);
            repository.EnsureInitialized();
            return new UndoManager(repository, refName, null, maxTransactions, logger, timeProvider);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Undo snapshots disabled: {Message}", ex.Message);
            return new UndoManager(
                null,
                refName,
                $"Undo is unavailable: the snapshot store could not be prepared ({ex.Message}).",
                maxTransactions,
                logger,
                timeProvider);
        }
    }

    /// <summary>True when turns can be snapshotted in this workspace.</summary>
    public bool IsAvailable => _repository is not null;

    /// <summary>Explains why undo is off, or null when it is on.</summary>
    public string? UnavailableReason { get; }

    /// <summary>The shadow repository backing this manager, for diagnostics and tests.</summary>
    public ShadowGitRepository? Repository => _repository;

    /// <summary>The snapshot ref owned by this session.</summary>
    public string RefName => _refName;

    /// <summary>True while a turn is in flight; /undo and /redo refuse in that window.</summary>
    public bool IsTurnActive
    {
        get { lock (_lock) { return _activeTurn is not null; } }
    }

    public bool CanUndo
    {
        get { lock (_lock) { return _activeTurn is null && _undo.Count > 0; } }
    }

    public bool CanRedo
    {
        get { lock (_lock) { return _activeTurn is null && _redo.Count > 0; } }
    }

    public int UndoDepth
    {
        get { lock (_lock) { return _undo.Count; } }
    }

    public int RedoDepth
    {
        get { lock (_lock) { return _redo.Count; } }
    }

    /// <summary>
    /// Snapshots the workspace before a turn runs. Returns null when snapshots are
    /// unavailable; the turn still runs, but nothing about it becomes undoable.
    /// </summary>
    public TurnHandle? BeginTurn(string prompt)
    {
        lock (_lock)
        {
            if (_activeTurn is not null)
            {
                return null;
            }

            // A new turn always invalidates the redo branch.
            _redo.Clear();

            if (_repository is null)
            {
                return null;
            }

            try
            {
                var pre = _repository.CaptureSnapshot(_refName, "andy: pre-turn snapshot");
                _activeTurn = new TurnHandle(prompt ?? string.Empty, pre);
                return _activeTurn;
            }
            catch (Exception ex)
            {
                // Without a reliable pre-image the turn's changes are invisible to the
                // history, and older entries would restore over them. Drop everything.
                _logger?.LogWarning(ex, "Pre-turn snapshot failed; undo history cleared.");
                _undo.Clear();
                return null;
            }
        }
    }

    /// <summary>
    /// Closes a turn and records it when it changed the workspace. A turn that
    /// changed nothing records nothing.
    /// </summary>
    public UndoTransaction? CompleteTurn(TurnHandle? handle)
    {
        lock (_lock)
        {
            if (handle is null || !ReferenceEquals(handle, _activeTurn))
            {
                _activeTurn = null;
                return null;
            }
            _activeTurn = null;

            if (_repository is null)
            {
                return null;
            }

            try
            {
                var post = _repository.CaptureSnapshot(_refName, "andy: post-turn snapshot");
                var changed = _repository.ChangedPaths(handle.PreSnapshot, post);
                if (changed.Count == 0)
                {
                    return null;
                }

                var transaction = new UndoTransaction(
                    handle.Prompt,
                    handle.PreSnapshot,
                    post,
                    changed,
                    _clock.GetUtcNow());
                _undo.Add(transaction);
                while (_undo.Count > _maxTransactions)
                {
                    _undo.RemoveAt(0);
                }
                return transaction;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Post-turn snapshot failed; undo history cleared.");
                _undo.Clear();
                _redo.Clear();
                return null;
            }
        }
    }

    /// <summary>
    /// Discards an in-flight turn. Interrupted, cancelled and failed turns go through
    /// here so they never leave a half-recorded transaction behind.
    /// </summary>
    public void AbortTurn(TurnHandle? handle)
    {
        lock (_lock)
        {
            if (handle is null || ReferenceEquals(handle, _activeTurn))
            {
                _activeTurn = null;
            }
        }
    }

    /// <summary>Reverts the most recent recorded turn.</summary>
    public UndoOutcome Undo()
    {
        lock (_lock)
        {
            if (_repository is null)
            {
                return new UndoOutcome(false, UnavailableReason ?? NotGitWorkspaceReason);
            }
            if (_activeTurn is not null)
            {
                return new UndoOutcome(false, "Undo is unavailable while a turn is still running. Wait for it to finish and try again.");
            }
            if (_undo.Count == 0)
            {
                return new UndoOutcome(false, "Nothing to undo: no turn in this session has changed files yet.");
            }

            var transaction = _undo[^1];
            try
            {
                _repository.RestorePaths(transaction.PreSnapshot, transaction.ChangedPaths);
            }
            catch (SnapshotException ex)
            {
                return new UndoOutcome(false, $"Undo refused: {ex.Message}");
            }

            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(transaction);
            return new UndoOutcome(
                true,
                $"Reverted the last turn ({Describe(transaction.ChangedPaths.Count)}). Use /redo to reapply.",
                transaction.Prompt,
                transaction.ChangedPaths);
        }
    }

    /// <summary>Reapplies the most recently undone turn.</summary>
    public UndoOutcome Redo()
    {
        lock (_lock)
        {
            if (_repository is null)
            {
                return new UndoOutcome(false, UnavailableReason ?? NotGitWorkspaceReason);
            }
            if (_activeTurn is not null)
            {
                return new UndoOutcome(false, "Redo is unavailable while a turn is still running. Wait for it to finish and try again.");
            }
            if (_redo.Count == 0)
            {
                return new UndoOutcome(false, "Nothing to redo.");
            }

            var transaction = _redo[^1];
            try
            {
                _repository.RestorePaths(transaction.PostSnapshot, transaction.ChangedPaths);
            }
            catch (SnapshotException ex)
            {
                return new UndoOutcome(false, $"Redo refused: {ex.Message}");
            }

            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(transaction);
            while (_undo.Count > _maxTransactions)
            {
                _undo.RemoveAt(0);
            }
            return new UndoOutcome(
                true,
                $"Reapplied the last undone turn ({Describe(transaction.ChangedPaths.Count)}).",
                null,
                transaction.ChangedPaths);
        }
    }

    /// <summary>Drops the session's snapshots and prunes the unreachable objects.</summary>
    public void Cleanup()
    {
        ShadowGitRepository? repository;
        lock (_lock)
        {
            _undo.Clear();
            _redo.Clear();
            _activeTurn = null;
            repository = _repository;
        }

        if (repository is null)
        {
            return;
        }

        try
        {
            repository.DeleteSessionSnapshots(_refName);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Snapshot cleanup failed for {Ref}", _refName);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        Cleanup();
    }

    private static string Describe(int count) =>
        count == 1 ? "1 file" : $"{count} files";
}
