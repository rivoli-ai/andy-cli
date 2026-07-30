using System;
using System.IO;

namespace Andy.Cli.Lsp;

/// <summary>
/// Keeps language servers inside the active workspace.
///
/// A language server is a long-lived child process that reads whatever we hand it, so the set of
/// paths we are willing to name is a real trust boundary. Two rules apply:
///
/// 1. A changed file is only forwarded when it resolves inside the workspace root. Symlinks are
///    resolved first, so a link inside the workspace pointing at /etc does not smuggle a path out.
/// 2. A server's project root is discovered by walking UP from the changed file and stopping at
///    the workspace root, so a stray marker file in a parent directory cannot pull the server's
///    root above the workspace.
///
/// Both rules are lifted only by an explicit opt-in (<c>allowOutsideWorkspace</c> in
/// .andy/lsp-servers.json), never implicitly.
/// </summary>
public static class LspWorkspaceGuard
{
    /// <summary>
    /// Whether <paramref name="candidatePath"/> resolves inside <paramref name="workspaceRoot"/>.
    /// Returns false for unresolvable paths rather than throwing: a guard that throws is a guard
    /// that can take down the agent loop.
    /// </summary>
    public static bool IsWithinWorkspace(string workspaceRoot, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var root = ResolveReal(workspaceRoot);
            var candidate = ResolveReal(candidatePath);

            if (string.Equals(root, candidate, PathComparison)) return true;

            var relative = Path.GetRelativePath(root, candidate);
            if (string.IsNullOrEmpty(relative) || relative == ".") return true;
            if (Path.IsPathRooted(relative)) return false;
            return !relative.StartsWith("..", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the nearest ancestor of <paramref name="filePath"/> (inclusive of its own directory)
    /// that contains one of <paramref name="rootMarkers"/>, never walking above
    /// <paramref name="workspaceRoot"/>. Falls back to the workspace root when nothing matches, and
    /// returns null when the file is not inside the workspace at all.
    /// </summary>
    public static string? FindProjectRoot(
        string workspaceRoot,
        string filePath,
        System.Collections.Generic.IReadOnlyList<string> rootMarkers)
    {
        if (!IsWithinWorkspace(workspaceRoot, filePath)) return null;

        string root;
        string current;
        try
        {
            root = ResolveReal(workspaceRoot);
            var resolvedFile = ResolveReal(filePath);
            current = Path.GetDirectoryName(resolvedFile) ?? root;
        }
        catch
        {
            return null;
        }

        if (rootMarkers.Count == 0) return root;

        // Bounded walk: at most the depth of the workspace, and always stops at the root.
        for (var guard = 0; guard < 256; guard++)
        {
            if (ContainsMarker(current, rootMarkers)) return current;
            if (string.Equals(current, root, PathComparison)) break;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, PathComparison)) break;

            // Never step outside the workspace, even if the loop above has not reached the root
            // exactly (different casing, trailing separators, mount quirks).
            if (!IsWithinWorkspace(root, parent)) break;
            current = parent;
        }

        return root;
    }

    private static bool ContainsMarker(string directory, System.Collections.Generic.IReadOnlyList<string> markers)
    {
        foreach (var marker in markers)
        {
            if (string.IsNullOrWhiteSpace(marker)) continue;
            try
            {
                if (marker.Contains('*') || marker.Contains('?'))
                {
                    if (Directory.EnumerateFileSystemEntries(directory, marker, SearchOption.TopDirectoryOnly)
                        .GetEnumerator().MoveNext())
                    {
                        return true;
                    }
                    continue;
                }

                var candidate = Path.Combine(directory, marker);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return true;
            }
            catch
            {
                // Unreadable directory: treat as "no marker here" and keep walking.
            }
        }

        return false;
    }

    /// <summary>
    /// Full path with symlinks resolved. <see cref="Path.GetFullPath(string)"/> alone normalizes
    /// "..", which is not enough: a symlink inside the workspace can still point anywhere.
    /// </summary>
    private static string ResolveReal(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0) full = Path.GetFullPath(path);

        try
        {
            var info = Directory.Exists(full) ? new DirectoryInfo(full) : (FileSystemInfo)new FileInfo(full);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                full = Path.GetFullPath(target.FullName)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            else
            {
                // The leaf itself is not a link, but an ancestor directory may be. Resolve the
                // containing directory so /tmp -> /private/tmp style links compare equal.
                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    var directoryTarget = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true);
                    if (directoryTarget is not null)
                    {
                        full = Path.Combine(directoryTarget.FullName, Path.GetFileName(full));
                    }
                }
            }
        }
        catch
        {
            // Non-existent or unreadable paths keep their normalized form.
        }

        return full;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
