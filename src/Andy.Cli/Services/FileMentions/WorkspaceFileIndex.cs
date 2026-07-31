using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Andy.Cli.Services.FileMentions;

/// <summary>A file or directory discovered under the workspace root.</summary>
/// <param name="RelativePath">
/// Workspace-relative path using forward slashes. Directories carry no trailing slash.
/// </param>
/// <param name="IsDirectory">True when the entry is a directory.</param>
public readonly record struct WorkspaceEntry(string RelativePath, bool IsDirectory);

/// <summary>
/// Lists the files and directories under a workspace root that are eligible for @-mentions,
/// skipping anything <see cref="WorkspaceIgnoreRules"/> excludes. The listing is cached for a
/// short window so typing a query does not re-walk the tree on every keystroke.
/// </summary>
public sealed class WorkspaceFileIndex
{
    /// <summary>Default upper bound on entries collected during a walk.</summary>
    public const int DefaultMaxEntries = 20_000;

    private readonly object _gate = new();
    private readonly string _root;
    private readonly WorkspaceIgnoreRules _ignoreRules;
    private readonly int _maxEntries;
    private readonly TimeSpan _cacheDuration;

    private IReadOnlyList<WorkspaceEntry>? _cached;
    private long _cachedAtTicks;
    private bool _truncated;

    public WorkspaceFileIndex(
        string root,
        WorkspaceIgnoreRules? ignoreRules = null,
        int maxEntries = DefaultMaxEntries,
        TimeSpan? cacheDuration = null)
    {
        _root = string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : Path.GetFullPath(root);
        _ignoreRules = ignoreRules ?? new WorkspaceIgnoreRules(_root);
        _maxEntries = Math.Max(1, maxEntries);
        _cacheDuration = cacheDuration ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Absolute workspace root.</summary>
    public string Root => _root;

    /// <summary>Ignore rules applied while walking.</summary>
    public WorkspaceIgnoreRules IgnoreRules => _ignoreRules;

    /// <summary>True when the last walk stopped at the entry cap.</summary>
    public bool WasTruncated
    {
        get { lock (_gate) { return _truncated; } }
    }

    /// <summary>Force the next <see cref="GetEntries"/> call to re-walk the tree.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
            _ignoreRules.Invalidate();
        }
    }

    /// <summary>All eligible entries, breadth-first from the workspace root.</summary>
    public IReadOnlyList<WorkspaceEntry> GetEntries()
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_cached is not null &&
                TimeSpan.FromSeconds((now - _cachedAtTicks) / (double)Stopwatch.Frequency) < _cacheDuration)
            {
                return _cached;
            }

            _cached = Walk();
            _cachedAtTicks = now;
            return _cached;
        }
    }

    private IReadOnlyList<WorkspaceEntry> Walk()
    {
        var results = new List<WorkspaceEntry>();
        _truncated = false;

        if (!Directory.Exists(_root))
        {
            return results;
        }

        // Breadth-first so shallow (usually more relevant) paths are collected before the cap
        // is reached in very large trees.
        var queue = new Queue<string>();
        queue.Enqueue(string.Empty);
        var visited = new HashSet<string>(CodeIndexPaths.Comparer);

        while (queue.Count > 0)
        {
            string relativeDirectory = queue.Dequeue();
            string absoluteDirectory = relativeDirectory.Length == 0
                ? _root
                : Path.Combine(_root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

            string canonical;
            try
            {
                canonical = Path.GetFullPath(absoluteDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!visited.Add(canonical))
            {
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(absoluteDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (results.Count >= _maxEntries)
                {
                    _truncated = true;
                    return results;
                }

                string name = Path.GetFileName(child);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                bool isDirectory;
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                    isDirectory = (attributes & FileAttributes.Directory) != 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                string relativeChild = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
                if (_ignoreRules.IsIgnored(relativeChild, isDirectory))
                {
                    continue;
                }

                results.Add(new WorkspaceEntry(relativeChild, isDirectory));

                // Do not descend through symlinked directories: they can form cycles and can
                // point outside the workspace, which would defeat containment.
                if (isDirectory && (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    queue.Enqueue(relativeChild);
                }
            }
        }

        return results;
    }
}
