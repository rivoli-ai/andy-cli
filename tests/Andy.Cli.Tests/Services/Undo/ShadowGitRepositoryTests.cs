using System;
using System.IO;
using System.Linq;
using System.Text;
using Andy.Cli.Services.Undo;
using Xunit;

namespace Andy.Cli.Tests.Services.Undo;

/// <summary>
/// Covers the snapshot store itself: where it stores objects, what it captures,
/// and the guarantee that it never touches the user's Git state (issue #276).
/// </summary>
public class ShadowGitRepositoryTests
{
    [Fact]
    public void WorkspaceId_IsStableAndPathSpecific()
    {
        var a = ShadowGitRepository.ComputeWorkspaceId("/tmp/projects/alpha");
        var b = ShadowGitRepository.ComputeWorkspaceId("/tmp/projects/alpha");
        var c = ShadowGitRepository.ComputeWorkspaceId("/tmp/other/alpha");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("alpha-", a);
        Assert.All(a, ch => Assert.True(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'));
    }

    [Fact]
    public void IsGitWorkspace_TrueForRepository_FalseForPlainDirectory()
    {
        using var repo = TempGitWorkspace.CreateGitRepository();
        using var plain = TempGitWorkspace.CreatePlainDirectory();

        Assert.True(ShadowGitRepository.IsGitWorkspace(repo.WorkspacePath));
        Assert.False(ShadowGitRepository.IsGitWorkspace(plain.WorkspacePath));
        Assert.False(ShadowGitRepository.IsGitWorkspace(Path.Combine(plain.WorkspacePath, "missing")));
    }

    [Fact]
    public void Snapshots_AreStoredOutsideTheWorkspace()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one");
        workspace.CommitAll("init");

        var shadow = new ShadowGitRepository(workspace.WorkspacePath, workspace.SnapshotRoot);
        shadow.EnsureInitialized();
        shadow.CaptureSnapshot(ShadowGitRepository.RefForSession("s1"), "snap");

        Assert.StartsWith(workspace.SnapshotRoot, shadow.GitDirectory);
        Assert.False(shadow.GitDirectory.StartsWith(workspace.WorkspacePath, StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(shadow.GitDirectory, "objects")));

        // Nothing new appears inside the workspace itself.
        var workspaceEntries = Directory
            .EnumerateFileSystemEntries(workspace.WorkspacePath)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { ".git", "a.txt" }, workspaceEntries);
    }

    [Fact]
    public void Snapshot_ExcludesTheUserGitDirectory()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one");
        workspace.CommitAll("init");

        var shadow = new ShadowGitRepository(workspace.WorkspacePath, workspace.SnapshotRoot);
        var commit = shadow.CaptureSnapshot(ShadowGitRepository.RefForSession("s1"), "snap");

        var paths = workspace
            .GitDir(shadow.GitDirectory, "ls-tree", "-r", "--name-only", commit)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("a.txt", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith(".git/", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_SkipsIgnoredFiles()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile(".gitignore", "build/\nsecret.env\n");
        workspace.WriteFile("a.txt", "one");
        workspace.CommitAll("init");
        workspace.WriteFile("secret.env", "TOKEN=abc");
        workspace.WriteFile("build/output.bin", "artifact");

        var shadow = new ShadowGitRepository(workspace.WorkspacePath, workspace.SnapshotRoot);
        var commit = shadow.CaptureSnapshot(ShadowGitRepository.RefForSession("s1"), "snap");

        var paths = workspace
            .GitDir(shadow.GitDirectory, "ls-tree", "-r", "--name-only", commit)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("a.txt", paths);
        Assert.DoesNotContain("secret.env", paths);
        Assert.DoesNotContain("build/output.bin", paths);
    }

    [Fact]
    public void RestorePaths_RoundTripsBinaryContentExactly()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        var original = new byte[] { 0x00, 0x01, 0xFF, 0x0A, 0x0D, 0x00, 0x42 };
        workspace.WriteBytes("data.bin", original);
        workspace.CommitAll("init");

        var shadow = new ShadowGitRepository(workspace.WorkspacePath, workspace.SnapshotRoot);
        var reference = ShadowGitRepository.RefForSession("s1");
        var before = shadow.CaptureSnapshot(reference, "before");

        workspace.WriteBytes("data.bin", new byte[] { 0x99 });
        var after = shadow.CaptureSnapshot(reference, "after");

        var changed = shadow.ChangedPaths(before, after);
        Assert.Equal(new[] { "data.bin" }, changed);

        shadow.RestorePaths(before, changed);
        Assert.Equal(original, workspace.ReadBytes("data.bin"));
    }

    [Fact]
    public void RestorePaths_RefusesUnknownSnapshot()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one");
        workspace.CommitAll("init");

        var shadow = new ShadowGitRepository(workspace.WorkspacePath, workspace.SnapshotRoot);
        shadow.EnsureInitialized();

        var ex = Assert.Throws<SnapshotException>(() =>
            shadow.RestorePaths(new string('0', 40), new[] { "a.txt" }));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("one", workspace.ReadFile("a.txt"));
    }

    [Fact]
    public void RestorePaths_RefusesPathsOutsideTheWorkspace()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one");
        workspace.CommitAll("init");

        var shadow = new ShadowGitRepository(workspace.WorkspacePath, workspace.SnapshotRoot);
        var commit = shadow.CaptureSnapshot(ShadowGitRepository.RefForSession("s1"), "snap");

        Assert.Throws<SnapshotException>(() =>
            shadow.RestorePaths(commit, new[] { "../escape.txt" }));
    }

    [Fact]
    public void SnapshotAndRestore_NeverTouchTheUserGitState()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("tracked.txt", "committed");
        workspace.CommitAll("init");

        // Warm the user's index so a later status call cannot be blamed for a diff.
        workspace.PorcelainStatus();
        var stateBefore = workspace.CaptureUserGitState();
        var indexBefore = workspace.CaptureIndexHash();

        var shadow = new ShadowGitRepository(workspace.WorkspacePath, workspace.SnapshotRoot);
        var reference = ShadowGitRepository.RefForSession("s1");
        var before = shadow.CaptureSnapshot(reference, "before");
        workspace.WriteFile("tracked.txt", "agent edit");
        var after = shadow.CaptureSnapshot(reference, "after");
        shadow.RestorePaths(before, shadow.ChangedPaths(before, after));

        Assert.Equal(indexBefore, workspace.CaptureIndexHash());
        Assert.Equal(stateBefore, workspace.CaptureUserGitState());
        Assert.Equal("committed", workspace.ReadFile("tracked.txt"));
    }

    [Fact]
    public void DeleteSessionSnapshots_RemovesOnlyTheOwningSessionRef()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one");
        workspace.CommitAll("init");

        var shadow = new ShadowGitRepository(workspace.WorkspacePath, workspace.SnapshotRoot);
        var refA = ShadowGitRepository.RefForSession("session-a");
        var refB = ShadowGitRepository.RefForSession("session-b");
        var commitA = shadow.CaptureSnapshot(refA, "a");
        var commitB = shadow.CaptureSnapshot(refB, "b");

        shadow.DeleteSessionSnapshots(refA);

        Assert.Null(shadow.TryResolveRef(refA));
        Assert.NotNull(shadow.TryResolveRef(refB));
        Assert.Equal(new[] { refB }, shadow.ListSessionRefs());
        Assert.Null(shadow.TryResolveRef(commitA));
        Assert.NotNull(shadow.TryResolveRef(commitB));
    }

    [Fact]
    public void RefForSession_SanitizesTheSessionId()
    {
        Assert.Equal(ShadowGitRepository.RefPrefix + "20260101-101010-ab12",
            ShadowGitRepository.RefForSession("20260101-101010-ab12"));
        Assert.Equal(ShadowGitRepository.RefPrefix + "a_b_c",
            ShadowGitRepository.RefForSession("a/b c"));
        Assert.Equal(ShadowGitRepository.RefPrefix + "default",
            ShadowGitRepository.RefForSession(""));
    }
}
