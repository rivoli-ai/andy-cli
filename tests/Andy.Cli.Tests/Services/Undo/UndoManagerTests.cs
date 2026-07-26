using System;
using System.IO;
using System.Linq;
using Andy.Cli.Services.Undo;
using Xunit;

namespace Andy.Cli.Tests.Services.Undo;

/// <summary>
/// Behavioural coverage for the /undo and /redo transaction log (issue #276):
/// dirty worktrees, untracked and ignored files, creation, deletion and rename,
/// redo invalidation, interrupted turns, retention and cleanup.
/// </summary>
public class UndoManagerTests
{
    private static UndoManager CreateManager(TempGitWorkspace workspace, string sessionId = "test-session", int maxTransactions = UndoManager.DefaultMaxTransactions) =>
        UndoManager.Create(
            workspace.WorkspacePath,
            sessionId,
            workspace.SnapshotRoot,
            maxTransactions);

    [Fact]
    public void NonGitWorkspace_IsUnsupportedWithAnActionableMessage()
    {
        using var workspace = TempGitWorkspace.CreatePlainDirectory();
        using var manager = CreateManager(workspace);

        Assert.False(manager.IsAvailable);
        Assert.NotNull(manager.UnavailableReason);
        Assert.Contains("git init", manager.UnavailableReason!, StringComparison.OrdinalIgnoreCase);

        var outcome = manager.Undo();
        Assert.False(outcome.Success);
        Assert.Contains("Git repository", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(outcome.Message, ch => Assert.True(ch <= 127, "Message must stay plain ASCII"));
    }

    [Fact]
    public void Undo_RestoresModifiedCreatedDeletedAndRenamedFiles()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("modified.txt", "original\n");
        workspace.WriteFile("deleted.txt", "keep me\n");
        workspace.WriteFile("src/old-name.cs", "class Old {}\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace);
        var turn = manager.BeginTurn("please refactor");
        Assert.NotNull(turn);

        workspace.WriteFile("modified.txt", "changed by agent\n");
        workspace.WriteFile("created.txt", "brand new\n");
        workspace.DeleteFile("deleted.txt");
        workspace.MoveFile("src/old-name.cs", "src/new-name.cs");

        var transaction = manager.CompleteTurn(turn);
        Assert.NotNull(transaction);
        Assert.Equal(
            new[] { "created.txt", "deleted.txt", "modified.txt", "src/new-name.cs", "src/old-name.cs" },
            transaction!.ChangedPaths.OrderBy(p => p, StringComparer.Ordinal).ToArray());

        var outcome = manager.Undo();

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal("original\n", workspace.ReadFile("modified.txt"));
        Assert.Equal("keep me\n", workspace.ReadFile("deleted.txt"));
        Assert.Equal("class Old {}\n", workspace.ReadFile("src/old-name.cs"));
        Assert.False(workspace.Exists("created.txt"));
        Assert.False(workspace.Exists("src/new-name.cs"));
    }

    [Fact]
    public void Redo_RestoresTheExactPostTurnContents()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("modified.txt", "original\n");
        workspace.WriteFile("deleted.txt", "keep me\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace);
        var turn = manager.BeginTurn("do the thing");
        workspace.WriteFile("modified.txt", "changed by agent\n");
        workspace.WriteFile("created.txt", "brand new\n");
        workspace.DeleteFile("deleted.txt");
        manager.CompleteTurn(turn);

        Assert.True(manager.Undo().Success);
        Assert.True(manager.CanRedo);

        var redo = manager.Redo();

        Assert.True(redo.Success, redo.Message);
        Assert.Equal("changed by agent\n", workspace.ReadFile("modified.txt"));
        Assert.Equal("brand new\n", workspace.ReadFile("created.txt"));
        Assert.False(workspace.Exists("deleted.txt"));
        Assert.True(manager.CanUndo);
        Assert.False(manager.CanRedo);
    }

    [Fact]
    public void Undo_PreservesPreExistingDirtyAndUntrackedFilesByteForByte()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("tracked.txt", "committed\n");
        workspace.WriteFile("agent-target.txt", "before\n");
        workspace.CommitAll("init");

        // Pre-existing user state the agent never touches.
        workspace.WriteFile("tracked.txt", "user edit in flight\n");
        workspace.WriteFile("scratch.txt", "untracked user note\n");
        var dirtyBytes = workspace.ReadBytes("tracked.txt");
        var untrackedBytes = workspace.ReadBytes("scratch.txt");
        var dirtyWriteTime = File.GetLastWriteTimeUtc(workspace.Resolve("tracked.txt"));

        using var manager = CreateManager(workspace);
        var turn = manager.BeginTurn("edit only the agent target");
        workspace.WriteFile("agent-target.txt", "after\n");
        var transaction = manager.CompleteTurn(turn);

        Assert.Equal(new[] { "agent-target.txt" }, transaction!.ChangedPaths);
        Assert.True(manager.Undo().Success);

        Assert.Equal("before\n", workspace.ReadFile("agent-target.txt"));
        Assert.Equal(dirtyBytes, workspace.ReadBytes("tracked.txt"));
        Assert.Equal(untrackedBytes, workspace.ReadBytes("scratch.txt"));
        Assert.Equal(dirtyWriteTime, File.GetLastWriteTimeUtc(workspace.Resolve("tracked.txt")));
    }

    [Fact]
    public void Undo_LeavesIgnoredFilesUntouched()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile(".gitignore", "*.log\nbuild/\n");
        workspace.WriteFile("app.cs", "v1\n");
        workspace.CommitAll("init");
        workspace.WriteFile("debug.log", "pre-existing log\n");
        workspace.WriteFile("build/artifact.bin", "pre-existing artifact\n");

        using var manager = CreateManager(workspace);
        var turn = manager.BeginTurn("build it");
        workspace.WriteFile("app.cs", "v2\n");
        workspace.WriteFile("debug.log", "log written during the turn\n");
        workspace.WriteFile("build/artifact.bin", "rebuilt artifact\n");
        var transaction = manager.CompleteTurn(turn);

        Assert.Equal(new[] { "app.cs" }, transaction!.ChangedPaths);
        Assert.True(manager.Undo().Success);

        Assert.Equal("v1\n", workspace.ReadFile("app.cs"));
        // Ignored files are outside every transaction: undo neither reverts nor deletes them.
        Assert.Equal("log written during the turn\n", workspace.ReadFile("debug.log"));
        Assert.Equal("rebuilt artifact\n", workspace.ReadFile("build/artifact.bin"));
    }

    [Fact]
    public void Undo_RestoresThePromptForTheComposer()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace);
        var turn = manager.BeginTurn("rename everything to snake_case");
        workspace.WriteFile("a.txt", "two\n");
        manager.CompleteTurn(turn);

        var outcome = manager.Undo();

        Assert.True(outcome.Success);
        Assert.Equal("rename everything to snake_case", outcome.RestoredPrompt);
    }

    [Fact]
    public void NewTurnAfterUndo_InvalidatesRedo()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace);
        var first = manager.BeginTurn("first");
        workspace.WriteFile("a.txt", "two\n");
        manager.CompleteTurn(first);

        Assert.True(manager.Undo().Success);
        Assert.True(manager.CanRedo);

        var second = manager.BeginTurn("second");
        Assert.False(manager.CanRedo);
        workspace.WriteFile("b.txt", "new\n");
        manager.CompleteTurn(second);

        var redo = manager.Redo();
        Assert.False(redo.Success);
        Assert.Contains("Nothing to redo", redo.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterruptedTurn_ProducesNoUndoableTransaction()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace);
        var turn = manager.BeginTurn("half a job");
        workspace.WriteFile("a.txt", "partially written\n");
        manager.AbortTurn(turn);

        Assert.False(manager.CanUndo);
        Assert.Equal(0, manager.UndoDepth);
        var outcome = manager.Undo();
        Assert.False(outcome.Success);
        Assert.Contains("Nothing to undo", outcome.Message, StringComparison.OrdinalIgnoreCase);
        // The partial work is left exactly as the interrupted turn left it.
        Assert.Equal("partially written\n", workspace.ReadFile("a.txt"));
    }

    [Fact]
    public void UndoAndRedo_AreRefusedWhileATurnIsRunning()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace);
        var completed = manager.BeginTurn("first");
        workspace.WriteFile("a.txt", "two\n");
        manager.CompleteTurn(completed);

        var running = manager.BeginTurn("second");
        Assert.NotNull(running);
        Assert.True(manager.IsTurnActive);
        Assert.False(manager.CanUndo);

        var undo = manager.Undo();
        var redo = manager.Redo();

        Assert.False(undo.Success);
        Assert.Contains("still running", undo.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(redo.Success);
        Assert.Contains("still running", redo.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("two\n", workspace.ReadFile("a.txt"));

        manager.AbortTurn(running);
    }

    [Fact]
    public void TurnWithoutFileChanges_IsNotRecorded()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace);
        var turn = manager.BeginTurn("just answer a question");
        var transaction = manager.CompleteTurn(turn);

        Assert.Null(transaction);
        Assert.False(manager.CanUndo);
    }

    [Fact]
    public void History_IsBoundedByTheRetentionLimit()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "0\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace, maxTransactions: 2);
        for (int i = 1; i <= 4; i++)
        {
            var turn = manager.BeginTurn($"turn {i}");
            workspace.WriteFile("a.txt", $"{i}\n");
            manager.CompleteTurn(turn);
        }

        Assert.Equal(2, manager.UndoDepth);
        Assert.True(manager.Undo().Success);
        Assert.Equal("3\n", workspace.ReadFile("a.txt"));
        Assert.True(manager.Undo().Success);
        Assert.Equal("2\n", workspace.ReadFile("a.txt"));
        Assert.False(manager.Undo().Success);
    }

    [Fact]
    public void SuccessiveUndos_WalkBackThroughTheHistory()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "start\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace);
        var first = manager.BeginTurn("first");
        workspace.WriteFile("a.txt", "first\n");
        workspace.WriteFile("only-first.txt", "x\n");
        manager.CompleteTurn(first);

        var second = manager.BeginTurn("second");
        workspace.WriteFile("a.txt", "second\n");
        manager.CompleteTurn(second);

        Assert.True(manager.Undo().Success);
        Assert.Equal("first\n", workspace.ReadFile("a.txt"));
        Assert.True(workspace.Exists("only-first.txt"));

        Assert.True(manager.Undo().Success);
        Assert.Equal("start\n", workspace.ReadFile("a.txt"));
        Assert.False(workspace.Exists("only-first.txt"));

        Assert.True(manager.Redo().Success);
        Assert.Equal("first\n", workspace.ReadFile("a.txt"));
        Assert.True(workspace.Exists("only-first.txt"));
    }

    [Fact]
    public void Cleanup_DropsHistoryAndSessionSnapshots()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        var manager = CreateManager(workspace, "session-to-clean");
        var turn = manager.BeginTurn("first");
        workspace.WriteFile("a.txt", "two\n");
        manager.CompleteTurn(turn);
        Assert.True(manager.CanUndo);

        var repository = manager.Repository!;
        Assert.NotNull(repository.TryResolveRef(manager.RefName));

        manager.Dispose();

        Assert.False(manager.CanUndo);
        Assert.Null(repository.TryResolveRef(manager.RefName));
        Assert.Empty(repository.ListSessionRefs());
        // The workspace itself is untouched by cleanup.
        Assert.Equal("two\n", workspace.ReadFile("a.txt"));
    }

    [Fact]
    public void Cleanup_KeepsSnapshotsOwnedByOtherSessions()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        using var keeper = CreateManager(workspace, "session-keep");
        var keeperTurn = keeper.BeginTurn("keep me");
        workspace.WriteFile("a.txt", "two\n");
        keeper.CompleteTurn(keeperTurn);

        var leaving = CreateManager(workspace, "session-leave");
        var leavingTurn = leaving.BeginTurn("bye");
        workspace.WriteFile("b.txt", "three\n");
        leaving.CompleteTurn(leavingTurn);
        leaving.Dispose();

        Assert.Null(keeper.Repository!.TryResolveRef(leaving.RefName));
        Assert.NotNull(keeper.Repository!.TryResolveRef(keeper.RefName));
        Assert.True(keeper.Undo().Success);
        Assert.Equal("one\n", workspace.ReadFile("a.txt"));
    }

    [Fact]
    public void UndoAndRedo_NeverTouchTheUserGitState()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("tracked.txt", "committed\n");
        workspace.CommitAll("init");
        workspace.WriteFile("dirty.txt", "user work in progress\n");

        workspace.PorcelainStatus();
        var stateBefore = workspace.CaptureUserGitState();
        var indexBefore = workspace.CaptureIndexHash();

        using var manager = CreateManager(workspace);
        var turn = manager.BeginTurn("touch the tracked file");
        workspace.WriteFile("tracked.txt", "agent edit\n");
        manager.CompleteTurn(turn);

        Assert.True(manager.Undo().Success);
        Assert.Equal(indexBefore, workspace.CaptureIndexHash());
        Assert.True(manager.Redo().Success);
        Assert.Equal(indexBefore, workspace.CaptureIndexHash());
        Assert.True(manager.Undo().Success);

        Assert.Equal(indexBefore, workspace.CaptureIndexHash());
        Assert.Equal(stateBefore, workspace.CaptureUserGitState());
        Assert.Equal("committed\n", workspace.ReadFile("tracked.txt"));
        Assert.Equal("user work in progress\n", workspace.ReadFile("dirty.txt"));
        Assert.Equal(string.Empty, workspace.Git("stash", "list").Trim());
    }

    [Fact]
    public void Snapshots_AreStoredUnderTheConfiguredSnapshotRoot()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        using var manager = CreateManager(workspace);
        var turn = manager.BeginTurn("x");
        workspace.WriteFile("a.txt", "two\n");
        manager.CompleteTurn(turn);

        var gitDirectory = manager.Repository!.GitDirectory;
        Assert.StartsWith(workspace.SnapshotRoot, gitDirectory);
        Assert.True(Directory.Exists(gitDirectory));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspacePath, ".andy")));
    }

    [Fact]
    public void DefaultSnapshotRoot_IsUnderTheUserAndyDirectory()
    {
        var root = ShadowGitRepository.DefaultSnapshotRoot();

        Assert.EndsWith(Path.Combine(".andy", "snapshots"), root);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            root);
    }
}
