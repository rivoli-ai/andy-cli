using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Andy.Permissions.Model;

namespace Andy.Cli.Services;

/// <summary>
/// Risk level assigned to a single tool-permission request. Used to decide whether an
/// "auto-approve" mode may allow the action without asking, or whether the request is
/// sensitive enough that it must always be shown to the user.
/// </summary>
public enum ApprovalRisk
{
    /// <summary>Low-risk: safe to auto-approve in auto mode (e.g. reads, builds, in-project writes).</summary>
    Normal,

    /// <summary>
    /// High-risk: must always be confirmed by the user even in auto mode. Covers destructive
    /// operations outside the project root, version-control destruction, and database destruction.
    /// </summary>
    High,
}

/// <summary>
/// CLI-side heuristic that classifies a <see cref="PermissionRequest"/> as <see cref="ApprovalRisk.Normal"/>
/// or <see cref="ApprovalRisk.High"/> from the evaluated resources the permission engine produced.
///
/// This is an INTERIM stand-in for a first-class risk assessment that belongs downstream in the
/// Andy.Tools / Andy.Permissions libraries (each tool declaring the risk of the specific action it is
/// about to perform). Until that exists, the CLI must not let an auto-approve mode blanket-authorize
/// genuinely destructive actions, so this class applies a conservative, fail-closed set of rules over
/// the resource kind (Command / Path / Host) and value (the actual command line or filesystem path).
///
/// Design rules:
///  - Fail closed: anything unrecognized that smells destructive is High, not Normal.
///  - Path destruction is judged against the project root: deleting inside the project is Normal,
///    deleting outside it (or outside any path we can reason about) is High.
///  - The whole classifier is a pure function of the request so it is trivially unit-testable and so a
///    downstream engine risk score can replace its body without touching callers.
/// </summary>
public static class ApprovalRiskAssessor
{
    /// <summary>
    /// Classify <paramref name="request"/>. <paramref name="projectRoot"/> is the directory treated as
    /// "inside the project" for path-destruction decisions (typically the current working directory).
    /// </summary>
    public static ApprovalRisk Assess(PermissionRequest request, string projectRoot)
    {
        if (request is null)
        {
            return ApprovalRisk.High;
        }

        var resources = request.Evaluation?.Resources;
        if (resources is null || resources.Count == 0)
        {
            // Nothing to inspect: the tool gave us no resource signal. We cannot prove it is safe,
            // but an empty resource list generally means a read or a benign action; treat as Normal so
            // auto mode remains useful, while genuinely destructive tools always carry a Path/Command.
            return ApprovalRisk.Normal;
        }

        foreach (var resource in resources)
        {
            var access = resource.Access;
            switch (access.Kind)
            {
                case ResourceKind.Command:
                    if (IsHighRiskCommand(access.Value, projectRoot))
                    {
                        return ApprovalRisk.High;
                    }
                    break;

                case ResourceKind.Path:
                    if (IsHighRiskPath(request.ToolId, access.Value, projectRoot))
                    {
                        return ApprovalRisk.High;
                    }
                    break;

                case ResourceKind.Host:
                    // Network egress is not destructive to local state; leave Normal. A future
                    // engine risk score may elevate specific hosts (e.g. production DBs).
                    break;
            }
        }

        return ApprovalRisk.Normal;
    }

    /// <summary>True when a command line is destructive in a way auto mode must never allow.</summary>
    internal static bool IsHighRiskCommand(string command, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var trimmed = command.Trim();

        if (IsDatabaseDestruction(trimmed))
        {
            return true;
        }

        if (IsGitRepoDestruction(trimmed, projectRoot))
        {
            return true;
        }

        if (IsDangerousDelete(trimmed, projectRoot))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when a path-scoped request is high-risk. Only mutating tools reach here meaningfully;
    /// we elevate when the target is a version-control directory or lies outside the project root
    /// for a destructive (delete/move) tool.
    /// </summary>
    internal static bool IsHighRiskPath(string toolId, string path, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (IsGitMetadataPath(path))
        {
            return true;
        }

        if (IsSensitivePath(path))
        {
            return true;
        }

        // Destructive file tools acting outside the project root are always confirmed.
        if (IsDestructiveFileTool(toolId) && !IsWithinRoot(path, projectRoot))
        {
            return true;
        }

        return false;
    }

    private static bool IsDestructiveFileTool(string toolId) =>
        toolId is "delete_file" or "move_file";

    /// <summary>Detects `rm`/`del`/`Remove-Item` style deletes that escape the project root or target VCS.</summary>
    private static bool IsDangerousDelete(string command, string projectRoot)
    {
        var tokens = Tokenize(command);
        if (tokens.Count == 0)
        {
            return false;
        }

        var exe = BaseName(tokens[0]);
        bool isDelete = exe is "rm" or "del" or "erase" or "rmdir" or "rd" or "Remove-Item" or "shred";
        if (!isDelete)
        {
            return false;
        }

        // Collect the non-flag arguments (candidate targets). Flags like -rf are skipped.
        var targets = tokens.Skip(1).Where(t => !t.StartsWith("-", StringComparison.Ordinal)).ToList();
        if (targets.Count == 0)
        {
            // e.g. bare `rm -rf` with no parsed target: cannot prove safe -> High.
            return true;
        }

        foreach (var target in targets)
        {
            if (IsGitMetadataPath(target))
            {
                return true;
            }
            if (!IsWithinRoot(target, projectRoot))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Detects destruction of the git repository itself (.git deletion or `git` repo-wiping).</summary>
    private static bool IsGitRepoDestruction(string command, string projectRoot)
    {
        // Any delete whose target is a .git path is caught by IsDangerousDelete too, but also catch
        // forms like `rm -rf .git` written without a path separator, and `git clean`-style repo wipes
        // that destroy history/working tree beyond recovery.
        if (command.Contains(".git", StringComparison.Ordinal))
        {
            var tokens = Tokenize(command);
            var exe = tokens.Count > 0 ? BaseName(tokens[0]) : string.Empty;
            if (exe is "rm" or "del" or "erase" or "rmdir" or "rd" or "Remove-Item" or "shred")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Detects database destruction statements across common CLIs and shells.</summary>
    private static bool IsDatabaseDestruction(string command)
    {
        // Lower-cased scan for destructive DB verbs. This is intentionally broad: any of these is a
        // strong "destroy data" signal regardless of which CLI (psql, mysql, mongo, sqlcmd, sqlite3,
        // or a raw `... -c "DROP DATABASE x"` invocation) carries it.
        var c = command.ToLowerInvariant();

        string[] destructive =
        {
            "drop database",
            "drop schema",
            "drop table",
            "truncate table",
            "dropcollection",
            "drop_database",
            "deletedatabase",
            "destroy",          // terraform destroy, kubectl ... destroy
        };

        foreach (var needle in destructive)
        {
            if (c.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // mongo/mongosh `.dropDatabase()` and redis FLUSHALL/FLUSHDB.
        if (c.Contains("dropdatabase(", StringComparison.Ordinal) ||
            c.Contains("flushall", StringComparison.Ordinal) ||
            c.Contains("flushdb", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>True when the path refers to git version-control metadata (.git and anything under it).</summary>
    private static bool IsGitMetadataPath(string path)
    {
        var p = path.Replace('\\', '/');
        // Matches ".git", ".git/", "x/.git", "x/.git/...", trailing or rooted forms.
        return p.Equals(".git", StringComparison.Ordinal)
            || p.StartsWith(".git/", StringComparison.Ordinal)
            || p.EndsWith("/.git", StringComparison.Ordinal)
            || p.Contains("/.git/", StringComparison.Ordinal);
    }

    /// <summary>Sensitive credential/config roots that must always be confirmed (mirrors Builtin denies).</summary>
    private static bool IsSensitivePath(string path)
    {
        var p = path.Replace('\\', '/');
        string[] roots =
        {
            "/.ssh/", "/.aws/", "/.gnupg/", "/.config/gcloud/", "/.kube/",
            "/etc/", "/root/",
        };
        foreach (var root in roots)
        {
            if (p.Contains(root, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="path"/> resolves to a location inside <paramref name="root"/>.
    /// Relative paths are treated as relative to the root. Paths that cannot be resolved, or that
    /// escape the root (.., absolute elsewhere, home ~), are outside.
    /// </summary>
    internal static bool IsWithinRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // A leading ~ is the user's home, never the project root.
        if (path.StartsWith("~", StringComparison.Ordinal))
        {
            return false;
        }

        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = Path.GetFullPath(root);
            fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(fullRoot, path));
        }
        catch (Exception)
        {
            // Unparseable path: cannot prove it is inside -> treat as outside (fail closed).
            return false;
        }

        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSep, StringComparison.Ordinal)
            || string.Equals(fullPath, fullRoot, StringComparison.Ordinal);
    }

    private static string BaseName(string token)
    {
        var t = token.Replace('\\', '/');
        int idx = t.LastIndexOf('/');
        return idx >= 0 ? t[(idx + 1)..] : t;
    }

    /// <summary>Minimal whitespace tokenizer; strips surrounding quotes from tokens.</summary>
    private static List<string> Tokenize(string command)
    {
        var result = new List<string>();
        foreach (var raw in command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim().Trim('"', '\'');
            if (t.Length > 0)
            {
                result.Add(t);
            }
        }
        return result;
    }
}
