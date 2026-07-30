using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Andy.Cli.Tests.Services.Undo;

/// <summary>
/// A throwaway Git repository plus an isolated snapshot root, used by the /undo
/// tests. Every git invocation is hermetic (no user or system config, explicit
/// identity) so the tests behave the same on any machine.
/// </summary>
internal sealed class TempGitWorkspace : IDisposable
{
    private readonly string _root;

    private TempGitWorkspace(string root, bool initializeGit)
    {
        _root = root;
        WorkspacePath = Path.Combine(root, "workspace");
        SnapshotRoot = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(WorkspacePath);
        Directory.CreateDirectory(SnapshotRoot);

        if (initializeGit)
        {
            Git("init", "--quiet");
            Git("config", "user.email", "test@example.com");
            Git("config", "user.name", "Test User");
            Git("config", "commit.gpgsign", "false");
        }
    }

    public string WorkspacePath { get; }

    public string SnapshotRoot { get; }

    public static TempGitWorkspace CreateGitRepository() =>
        new(NewRoot(), initializeGit: true);

    public static TempGitWorkspace CreatePlainDirectory() =>
        new(NewRoot(), initializeGit: false);

    /// <summary>Writes a workspace file (creating parent directories).</summary>
    public void WriteFile(string relativePath, string content)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void WriteBytes(string relativePath, byte[] content)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public string ReadFile(string relativePath) => File.ReadAllText(Resolve(relativePath));

    public byte[] ReadBytes(string relativePath) => File.ReadAllBytes(Resolve(relativePath));

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public void DeleteFile(string relativePath) => File.Delete(Resolve(relativePath));

    public void MoveFile(string from, string to)
    {
        var target = Resolve(to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(Resolve(from), target);
    }

    public string Resolve(string relativePath) =>
        Path.Combine(WorkspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Stages everything and commits, so the repository has real tracked files.</summary>
    public void CommitAll(string message)
    {
        Git("add", "--all");
        Git("commit", "--quiet", "-m", message);
    }

    /// <summary>Runs git inside the workspace (the user's repository).</summary>
    public string Git(params string[] arguments) => Run(WorkspacePath, arguments);

    /// <summary>Runs git against an explicit git directory (used to inspect shadow repositories).</summary>
    public string GitDir(string gitDirectory, params string[] arguments) =>
        Run(WorkspacePath, new[] { "--git-dir", gitDirectory }.Concat(arguments).ToArray());

    /// <summary>A fingerprint of the user's Git state that must never change.</summary>
    public UserGitState CaptureUserGitState()
    {
        var gitDir = Path.Combine(WorkspacePath, ".git");
        var indexPath = Path.Combine(gitDir, "index");
        return new UserGitState(
            File.Exists(indexPath) ? Hash(File.ReadAllBytes(indexPath)) : "no-index",
            File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim(),
            Run(WorkspacePath, new[] { "rev-parse", "HEAD" }).Trim(),
            string.Join("\n", Run(WorkspacePath, new[] { "show-ref" })
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(l => l, StringComparer.Ordinal)),
            Run(WorkspacePath, new[] { "stash", "list" }).Trim(),
            Run(WorkspacePath, new[] { "branch", "--show-current" }).Trim());
    }

    /// <summary>Index bytes only, captured without running any git command.</summary>
    public string CaptureIndexHash()
    {
        var indexPath = Path.Combine(WorkspacePath, ".git", "index");
        return File.Exists(indexPath) ? Hash(File.ReadAllBytes(indexPath)) : "no-index";
    }

    public string PorcelainStatus() => Run(WorkspacePath, new[] { "status", "--porcelain" });

    public void Dispose()
    {
        try
        {
            ForceDelete(_root);
        }
        catch (Exception)
        {
            // A leaked temp directory must never fail a test run.
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "andy-undo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var devNull = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment.Remove("GIT_DIR");
        startInfo.Environment.Remove("GIT_WORK_TREE");
        startInfo.Environment.Remove("GIT_INDEX_FILE");
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = devNull;
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = devNull;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_AUTHOR_NAME"] = "Test User";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "test@example.com";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "Test User";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        if (process.ExitCode != 0 && arguments.Count > 0 && arguments[0] != "rev-parse")
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {error}");
        }
        return output;
    }

    private static void ForceDelete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (Exception)
            {
                // Best effort; read-only pack files inside .git are the usual cause.
            }
        }
        Directory.Delete(directory, recursive: true);
    }
}

/// <summary>Fingerprint of the user's Git state used by the safety assertions.</summary>
internal sealed record UserGitState(
    string IndexHash,
    string Head,
    string HeadCommit,
    string Refs,
    string StashList,
    string Branch);
