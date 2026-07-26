using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services.Undo;

/// <summary>
/// A shadow Git repository used to snapshot a workspace without ever touching the
/// user's own Git state (issue #276).
///
/// The repository lives outside the workspace - by default under
/// <c>~/.andy/snapshots/&lt;workspace-id&gt;/</c> - and is driven with an explicit
/// <c>GIT_DIR</c> plus the workspace as <c>GIT_WORK_TREE</c>. Because every
/// invocation supplies its own <c>GIT_DIR</c>, <c>GIT_INDEX_FILE</c> and config
/// isolation, the user's index, refs, stash, branch and reflog are never read or
/// written. Snapshots are ordinary Git trees/commits kept alive by a per-session
/// ref under <c>refs/andy/</c> inside the shadow repository.
///
/// Restores never run a git command that writes into the work tree: file contents
/// are read out with <c>cat-file</c> and written by the CLI itself, so only the
/// paths that belong to a transaction are ever touched.
/// </summary>
public sealed class ShadowGitRepository
{
    /// <summary>Prefix for the per-session snapshot refs kept inside the shadow repository.</summary>
    public const string RefPrefix = "refs/andy/sessions/";

    private const string ExcludeHeader = "# Managed by Andy CLI shadow snapshots. Do not edit.";

    private static readonly Regex s_unsafeNameChars = new("[^A-Za-z0-9._-]", RegexOptions.Compiled);

    private readonly Dictionary<string, string?> _environment;

    public ShadowGitRepository(string workspacePath, string? snapshotRoot = null)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new ArgumentException("Workspace path is required.", nameof(workspacePath));
        }

        WorkspacePath = NormalizeDirectory(workspacePath);
        WorkspaceId = ComputeWorkspaceId(WorkspacePath);
        SnapshotRoot = snapshotRoot is null ? DefaultSnapshotRoot() : Path.GetFullPath(snapshotRoot);
        GitDirectory = Path.Combine(SnapshotRoot, WorkspaceId);

        var devNull = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_DIR"] = GitDirectory,
            ["GIT_WORK_TREE"] = WorkspacePath,
            ["GIT_INDEX_FILE"] = Path.Combine(GitDirectory, "index"),
            // Config isolation: no user/system config, hooks, templates, aliases or
            // signing settings can influence the shadow repository.
            ["GIT_CONFIG_GLOBAL"] = devNull,
            ["GIT_CONFIG_SYSTEM"] = devNull,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_AUTHOR_NAME"] = "Andy CLI",
            ["GIT_AUTHOR_EMAIL"] = "andy@localhost",
            ["GIT_COMMITTER_NAME"] = "Andy CLI",
            ["GIT_COMMITTER_EMAIL"] = "andy@localhost"
        };
    }

    /// <summary>Absolute, normalized path of the snapshotted workspace.</summary>
    public string WorkspacePath { get; }

    /// <summary>Stable, filesystem-safe identifier derived from the workspace path.</summary>
    public string WorkspaceId { get; }

    /// <summary>Root directory holding one shadow repository per workspace.</summary>
    public string SnapshotRoot { get; }

    /// <summary>The shadow GIT_DIR for this workspace.</summary>
    public string GitDirectory { get; }

    /// <summary>Default snapshot root: <c>~/.andy/snapshots</c>.</summary>
    public static string DefaultSnapshotRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".andy",
        "snapshots");

    /// <summary>
    /// Stable id for a workspace: the directory name plus a hash of its full path,
    /// so two checkouts with the same name never share a shadow repository.
    /// </summary>
    public static string ComputeWorkspaceId(string workspacePath)
    {
        var full = NormalizeDirectory(workspacePath);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(name))
        {
            name = "workspace";
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))
            .ToLowerInvariant()
            .Substring(0, 12);
        return $"{s_unsafeNameChars.Replace(name, "_")}-{hash}";
    }

    /// <summary>
    /// True when the path sits inside a real Git working tree. Runs with GIT_DIR and
    /// friends explicitly cleared so it reads the user's repository discovery rules
    /// only, and it never writes anything.
    /// </summary>
    public static bool IsGitWorkspace(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return false;
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
            ["GIT_INDEX_FILE"] = null,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0"
        };

        try
        {
            var result = GitProcess.Run(
                NormalizeDirectory(workspacePath),
                environment,
                new[] { "rev-parse", "--is-inside-work-tree" },
                timeoutMs: 15_000);
            return result.Success &&
                   result.Text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (SnapshotException)
        {
            return false;
        }
    }

    /// <summary>Creates the shadow repository if it does not exist yet.</summary>
    public void EnsureInitialized()
    {
        Directory.CreateDirectory(SnapshotRoot);
        RestrictToOwner(SnapshotRoot);
        Directory.CreateDirectory(GitDirectory);
        RestrictToOwner(GitDirectory);

        if (!File.Exists(Path.Combine(GitDirectory, "HEAD")))
        {
            Execute("init", "--quiet");
        }

        // The workspace's own .git directory is never part of a snapshot: the user's
        // repository state stays entirely out of the shadow object database.
        var infoDirectory = Path.Combine(GitDirectory, "info");
        Directory.CreateDirectory(infoDirectory);
        var excludePath = Path.Combine(infoDirectory, "exclude");
        var excludeContent = ExcludeHeader + "\n.git/\n";
        if (!File.Exists(excludePath) || File.ReadAllText(excludePath) != excludeContent)
        {
            File.WriteAllText(excludePath, excludeContent);
        }
    }

    /// <summary>
    /// Captures the current workspace state as a commit and points
    /// <paramref name="refName"/> at it (chaining onto the previous snapshot so all
    /// snapshots for the session stay reachable). Returns the commit id.
    /// </summary>
    public string CaptureSnapshot(string refName, string message)
    {
        EnsureInitialized();

        // Stages every tracked and untracked file that is not ignored. Ignored files
        // are deliberately left out: they are never snapshotted and never restored.
        Execute("add", "--all", "--", ".");
        var tree = Execute("write-tree").Trim();
        if (tree.Length == 0)
        {
            throw new SnapshotException("git write-tree returned no tree id.");
        }

        var parent = TryResolveRef(refName);
        var commitArgs = parent is null
            ? new[] { "commit-tree", tree, "-m", message }
            : new[] { "commit-tree", tree, "-p", parent, "-m", message };
        var commit = Execute(commitArgs).Trim();
        if (commit.Length == 0)
        {
            throw new SnapshotException("git commit-tree returned no commit id.");
        }

        Execute("update-ref", refName, commit);
        return commit;
    }

    /// <summary>Paths that differ between two snapshots (renames reported as delete + add).</summary>
    public IReadOnlyList<string> ChangedPaths(string fromCommit, string toCommit)
    {
        var output = Execute("diff", "--name-only", "-z", "--no-renames", fromCommit, toCommit);
        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Restores exactly <paramref name="paths"/> to the state recorded in
    /// <paramref name="commit"/>. Everything is validated and staged first; if any
    /// part of the plan cannot be satisfied nothing is written at all.
    /// </summary>
    public void RestorePaths(string commit, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }
        if (TryResolveRef(commit) is null)
        {
            throw new SnapshotException($"Snapshot {commit} is missing from the shadow repository.");
        }

        var plan = new List<RestoreStep>(paths.Count);
        foreach (var path in paths)
        {
            ValidateRelativePath(path);
            var entry = LookupEntry(commit, path);
            if (entry is null)
            {
                plan.Add(RestoreStep.Delete(path));
                continue;
            }
            if (!entry.Type.Equals("blob", StringComparison.Ordinal))
            {
                throw new SnapshotException(
                    $"Cannot restore '{path}': snapshots of submodules or nested trees are not supported.");
            }
            plan.Add(entry.Mode switch
            {
                "100644" or "100755" => RestoreStep.Write(path, entry.Sha, entry.Mode),
                "120000" => RestoreStep.Symlink(path, entry.Sha),
                _ => throw new SnapshotException(
                    $"Cannot restore '{path}': unsupported file mode {entry.Mode}.")
            });
        }

        // Phase 1: materialize every replacement next to its destination. A failure
        // here leaves the workspace untouched.
        var staged = new List<(RestoreStep Step, string TempPath)>();
        try
        {
            foreach (var step in plan)
            {
                if (step.Kind != RestoreKind.Write)
                {
                    continue;
                }
                var destination = ResolveWorkspacePath(step.Path);
                var directory = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(directory);
                var tempPath = Path.Combine(
                    directory,
                    $".andy-undo-{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(tempPath, ReadBlob(step.Sha!));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tempPath, step.Mode == "100755"
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                          UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                          UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite |
                          UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
                staged.Add((step, tempPath));
            }
        }
        catch (Exception ex)
        {
            foreach (var (_, tempPath) in staged)
            {
                TryDelete(tempPath);
            }
            if (ex is SnapshotException)
            {
                throw;
            }
            throw new SnapshotException($"Failed to prepare the restore: {ex.Message}", ex);
        }

        // Phase 2: apply. Moves are atomic within the workspace volume.
        foreach (var (step, tempPath) in staged)
        {
            var destination = ResolveWorkspacePath(step.Path);
            if (Directory.Exists(destination))
            {
                TryDelete(tempPath);
                throw new SnapshotException(
                    $"Cannot restore '{step.Path}': a directory now occupies that path.");
            }
            File.Move(tempPath, destination, overwrite: true);
        }

        foreach (var step in plan)
        {
            switch (step.Kind)
            {
                case RestoreKind.Delete:
                    RemoveFile(step.Path);
                    break;
                case RestoreKind.Symlink:
                    var destination = ResolveWorkspacePath(step.Path);
                    if (Directory.Exists(destination))
                    {
                        throw new SnapshotException(
                            $"Cannot restore '{step.Path}': a directory now occupies that path.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (FileOrLinkExists(destination))
                    {
                        File.Delete(destination);
                    }
                    File.CreateSymbolicLink(destination, Encoding.UTF8.GetString(ReadBlob(step.Sha!)));
                    break;
            }
        }
    }

    /// <summary>Reads a blob's exact bytes out of the shadow object database.</summary>
    public byte[] ReadBlob(string sha)
    {
        var result = GitProcess.Run(WorkspacePath, _environment, new[] { "cat-file", "blob", sha });
        if (!result.Success)
        {
            throw new SnapshotException($"Failed to read snapshot object {sha}: {result.Error.Trim()}");
        }
        return result.Output;
    }

    /// <summary>Resolves a ref or object id, returning null when it does not exist.</summary>
    public string? TryResolveRef(string reference)
    {
        var result = GitProcess.Run(
            WorkspacePath,
            _environment,
            new[] { "rev-parse", "--verify", "--quiet", reference + "^{commit}" });
        var text = result.Text.Trim();
        return result.Success && text.Length > 0 ? text : null;
    }

    /// <summary>
    /// Drops a session's snapshot ref and prunes anything no longer reachable. Other
    /// sessions' snapshots keep their own refs and survive.
    /// </summary>
    public void DeleteSessionSnapshots(string refName)
    {
        if (!Directory.Exists(GitDirectory))
        {
            return;
        }
        var existing = TryResolveRef(refName);
        if (existing is not null)
        {
            GitProcess.Run(WorkspacePath, _environment, new[] { "update-ref", "-d", refName });
        }
        GitProcess.Run(
            WorkspacePath,
            _environment,
            new[] { "gc", "--prune=now", "--quiet" },
            timeoutMs: 60_000);
    }

    /// <summary>Lists the session refs currently stored in this shadow repository.</summary>
    public IReadOnlyList<string> ListSessionRefs()
    {
        if (!Directory.Exists(GitDirectory))
        {
            return Array.Empty<string>();
        }
        var result = GitProcess.Run(
            WorkspacePath,
            _environment,
            new[] { "for-each-ref", "--format=%(refname)", RefPrefix });
        return result.Success
            ? result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray()
            : Array.Empty<string>();
    }

    /// <summary>The snapshot ref name owned by a session.</summary>
    public static string RefForSession(string sessionId)
    {
        var safe = s_unsafeNameChars.Replace(sessionId ?? string.Empty, "_");
        if (safe.Length == 0)
        {
            safe = "default";
        }
        return RefPrefix + safe;
    }

    private TreeEntry? LookupEntry(string commit, string path)
    {
        var result = GitProcess.Run(
            WorkspacePath,
            _environment,
            new[] { "ls-tree", "-z", commit, "--", path });
        if (!result.Success)
        {
            throw new SnapshotException(
                $"Failed to inspect snapshot {commit} for '{path}': {result.Error.Trim()}");
        }

        foreach (var record in result.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }
            var name = record[(tab + 1)..];
            if (!string.Equals(name, path, StringComparison.Ordinal))
            {
                continue;
            }
            var fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3)
            {
                throw new SnapshotException($"Unreadable snapshot entry for '{path}'.");
            }
            return new TreeEntry(fields[0], fields[1], fields[2]);
        }

        return null;
    }

    private void RemoveFile(string relativePath)
    {
        var destination = ResolveWorkspacePath(relativePath);
        if (Directory.Exists(destination))
        {
            throw new SnapshotException(
                $"Cannot remove '{relativePath}': a directory now occupies that path.");
        }
        if (FileOrLinkExists(destination))
        {
            File.Delete(destination);
        }
        PruneEmptyDirectories(Path.GetDirectoryName(destination));
    }

    /// <summary>True for regular files and for symlinks, including dangling ones.</summary>
    private static bool FileOrLinkExists(string path)
    {
        var info = new FileInfo(path);
        return info.Exists || info.LinkTarget is not null;
    }

    private void PruneEmptyDirectories(string? directory)
    {
        var root = WorkspacePath;
        while (!string.IsNullOrEmpty(directory) &&
               directory.Length > root.Length &&
               directory.StartsWith(root, StringComparison.Ordinal) &&
               Directory.Exists(directory))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return;
            }
            try
            {
                Directory.Delete(directory);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            directory = Path.GetDirectoryName(directory);
        }
    }

    private string ResolveWorkspacePath(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(
            WorkspacePath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(WorkspacePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new SnapshotException($"Refusing to restore '{relativePath}' outside the workspace.");
        }
        return combined;
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.Split('/').Any(segment => segment == ".."))
        {
            throw new SnapshotException($"Refusing to restore suspicious path '{path}'.");
        }
    }

    private string Execute(params string[] arguments)
    {
        var result = GitProcess.Run(WorkspacePath, _environment, arguments);
        if (!result.Success)
        {
            throw new SnapshotException(
                $"git {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.Error.Trim()}");
        }
        return result.Text;
    }

    private static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length > 1)
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar);
        }
        return full;
    }

    private static void RestrictToOwner(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception)
        {
            // Best effort only; snapshot correctness does not depend on it.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort cleanup of a staged temp file.
        }
    }

    private sealed record TreeEntry(string Mode, string Type, string Sha);

    private enum RestoreKind
    {
        Write,
        Delete,
        Symlink
    }

    private sealed record RestoreStep(RestoreKind Kind, string Path, string? Sha, string? Mode)
    {
        public static RestoreStep Write(string path, string sha, string mode) =>
            new(RestoreKind.Write, path, sha, mode);

        public static RestoreStep Delete(string path) =>
            new(RestoreKind.Delete, path, null, null);

        public static RestoreStep Symlink(string path, string sha) =>
            new(RestoreKind.Symlink, path, sha, "120000");
    }
}
