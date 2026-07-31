using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services.FileMentions;

/// <summary>
/// Evaluates whether a workspace-relative path is ignored, combining a built-in list of
/// always-skipped directory names with the repository's own <c>.gitignore</c> files
/// (root, nested, and <c>.git/info/exclude</c>).
///
/// This is intentionally a small, self-contained subset of the gitignore specification:
/// comments, negation (<c>!</c>), directory-only patterns (trailing <c>/</c>), anchored
/// patterns (containing a <c>/</c>), <c>*</c>, <c>**</c>, <c>?</c> and <c>[...]</c> classes.
/// It is used to keep private/generated files out of the @-mention picker, so a rule that is
/// slightly too aggressive is preferable to one that leaks a file.
/// </summary>
public sealed class WorkspaceIgnoreRules
{
    /// <summary>
    /// Directory names skipped regardless of what <c>.gitignore</c> says. These are either
    /// version-control metadata (never useful to attach) or build/dependency output that would
    /// swamp fuzzy search in a large repository.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultIgnoredDirectoryNames = new[]
    {
        ".git", ".hg", ".svn",
        "node_modules", "bower_components", "vendor",
        "bin", "obj", "dist", "build", "out", "target",
        ".vs", ".idea", ".gradle", ".tox",
        ".venv", "venv", "__pycache__", ".mypy_cache", ".pytest_cache",
        ".next", ".nuxt", ".turbo", ".parcel-cache",
        "TestResults", "coverage", ".andy"
    };

    private static readonly RegexOptions PatternRegexOptions =
        RegexOptions.CultureInvariant |
        (OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase);

    private readonly string _root;
    private readonly HashSet<string> _ignoredDirectoryNames;
    private readonly ConcurrentDictionary<string, IReadOnlyList<IgnoreRule>> _rulesByDirectory = new(StringComparer.Ordinal);

    /// <summary>
    /// Create ignore rules rooted at <paramref name="root"/>.
    /// </summary>
    /// <param name="root">Absolute workspace root.</param>
    /// <param name="ignoredDirectoryNames">
    /// Overrides <see cref="DefaultIgnoredDirectoryNames"/> when supplied. Pass an empty
    /// sequence to rely purely on <c>.gitignore</c>.
    /// </param>
    public WorkspaceIgnoreRules(string root, IEnumerable<string>? ignoredDirectoryNames = null)
    {
        _root = string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : Path.GetFullPath(root);
        _ignoredDirectoryNames = new HashSet<string>(
            ignoredDirectoryNames ?? DefaultIgnoredDirectoryNames,
            CodeIndexPaths.Comparer);
    }

    /// <summary>Absolute workspace root these rules are anchored to.</summary>
    public string Root => _root;

    /// <summary>Drop cached <c>.gitignore</c> content so the next query re-reads from disk.</summary>
    public void Invalidate() => _rulesByDirectory.Clear();

    /// <summary>
    /// True when a workspace-relative path (forward-slash separated, no leading slash) is ignored.
    /// A path is ignored when any of its ancestor directories is ignored.
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < segments.Length; i++)
        {
            bool segmentIsDirectory = i < segments.Length - 1 || isDirectory;
            if (segmentIsDirectory && _ignoredDirectoryNames.Contains(segments[i]))
            {
                return true;
            }

            string prefix = string.Join('/', segments.Take(i + 1));
            if (MatchesIgnoreRules(prefix, segmentIsDirectory))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the directory name alone is on the built-in skip list.</summary>
    public bool IsIgnoredDirectoryName(string name) => _ignoredDirectoryNames.Contains(name);

    private bool MatchesIgnoreRules(string relativePath, bool isDirectory)
    {
        var segments = relativePath.Split('/');
        bool ignored = false;

        // Shallow .gitignore files are evaluated first so that deeper ones (which git gives
        // higher precedence) can override them; within one file the last matching rule wins.
        for (int depth = 0; depth < segments.Length; depth++)
        {
            string ruleDirectory = string.Join('/', segments.Take(depth));
            string subPath = string.Join('/', segments.Skip(depth));
            foreach (var rule in GetRules(ruleDirectory))
            {
                if (rule.Matches(subPath, isDirectory))
                {
                    ignored = !rule.Negated;
                }
            }
        }

        return ignored;
    }

    private IReadOnlyList<IgnoreRule> GetRules(string relativeDirectory)
    {
        return _rulesByDirectory.GetOrAdd(relativeDirectory, LoadRules);
    }

    private IReadOnlyList<IgnoreRule> LoadRules(string relativeDirectory)
    {
        var rules = new List<IgnoreRule>();
        string absoluteDirectory = relativeDirectory.Length == 0
            ? _root
            : Path.Combine(_root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

        AddRulesFromFile(rules, Path.Combine(absoluteDirectory, ".gitignore"));
        if (relativeDirectory.Length == 0)
        {
            AddRulesFromFile(rules, Path.Combine(absoluteDirectory, ".git", "info", "exclude"));
        }

        return rules;
    }

    private static void AddRulesFromFile(List<IgnoreRule> rules, string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var line in lines)
        {
            var rule = IgnoreRule.TryParse(line);
            if (rule is not null)
            {
                rules.Add(rule);
            }
        }
    }

    private sealed class IgnoreRule
    {
        private readonly Regex _regex;

        private IgnoreRule(Regex regex, bool negated, bool directoryOnly)
        {
            _regex = regex;
            Negated = negated;
            DirectoryOnly = directoryOnly;
        }

        public bool Negated { get; }
        public bool DirectoryOnly { get; }

        public bool Matches(string relativePath, bool isDirectory)
        {
            if (DirectoryOnly && !isDirectory)
            {
                return false;
            }
            return _regex.IsMatch(relativePath);
        }

        public static IgnoreRule? TryParse(string rawLine)
        {
            if (rawLine is null)
            {
                return null;
            }

            // Trailing whitespace is not significant unless escaped; leading whitespace is kept
            // by git but trimming it is harmless for the patterns real repositories use.
            string line = rawLine.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                return null;
            }

            bool negated = false;
            if (line.StartsWith('!'))
            {
                negated = true;
                line = line.Substring(1);
            }
            else if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal))
            {
                line = line.Substring(1);
            }

            if (line.Length == 0)
            {
                return null;
            }

            bool directoryOnly = line.EndsWith('/');
            if (directoryOnly)
            {
                line = line.TrimEnd('/');
            }

            if (line.Length == 0)
            {
                return null;
            }

            bool anchored = line.Contains('/');
            if (line.StartsWith('/'))
            {
                line = line.TrimStart('/');
            }

            if (line.Length == 0)
            {
                return null;
            }

            string body = GlobToRegex(line);
            string prefix = anchored ? "^" : "^(?:.*/)?";
            Regex regex;
            try
            {
                regex = new Regex(prefix + body + "$", PatternRegexOptions);
            }
            catch (ArgumentException)
            {
                return null;
            }

            return new IgnoreRule(regex, negated, directoryOnly);
        }

        private static string GlobToRegex(string pattern)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < pattern.Length)
            {
                char c = pattern[i];
                if (c == '*')
                {
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i += 2;
                        if (i < pattern.Length && pattern[i] == '/')
                        {
                            sb.Append("(?:.*/)?");
                            i++;
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                        continue;
                    }

                    sb.Append("[^/]*");
                    i++;
                    continue;
                }

                if (c == '?')
                {
                    sb.Append("[^/]");
                    i++;
                    continue;
                }

                if (c == '[')
                {
                    int close = pattern.IndexOf(']', i + 1);
                    if (close > i)
                    {
                        string cls = pattern.Substring(i, close - i + 1);
                        // git uses '!' for class negation; regex uses '^'.
                        if (cls.Length > 1 && cls[1] == '!')
                        {
                            cls = "[^" + cls.Substring(2);
                        }
                        sb.Append(cls);
                        i = close + 1;
                        continue;
                    }
                }

                if (c == '\\' && i + 1 < pattern.Length)
                {
                    sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                    i += 2;
                    continue;
                }

                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }

            return sb.ToString();
        }
    }
}
