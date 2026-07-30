using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Services.FileMentions;

/// <summary>A ranked @-mention candidate.</summary>
/// <param name="RelativePath">Workspace-relative path with forward slashes.</param>
/// <param name="IsDirectory">True when the candidate is a directory.</param>
/// <param name="Score">Combined fuzzy + frecency score; higher is better.</param>
public sealed record FileMentionSuggestion(string RelativePath, bool IsDirectory, int Score)
{
    /// <summary>The text inserted into the composer when this suggestion is accepted.</summary>
    public string MentionText => FileMentionSyntax.FormatMention(RelativePath, IsDirectory);

    /// <summary>Display label: the file name, with its parent directory shown separately.</summary>
    public string DisplayName
    {
        get
        {
            int slash = RelativePath.LastIndexOf('/');
            string name = slash >= 0 ? RelativePath.Substring(slash + 1) : RelativePath;
            return IsDirectory ? name + "/" : name;
        }
    }

    /// <summary>Parent directory of the candidate, or an empty string at the workspace root.</summary>
    public string DirectoryName
    {
        get
        {
            int slash = RelativePath.LastIndexOf('/');
            return slash >= 0 ? RelativePath.Substring(0, slash) : string.Empty;
        }
    }
}

/// <summary>
/// Fuzzy search over the workspace file listing for the @-mention picker. Free of any TUI
/// dependency so headless and custom-command callers can reuse it.
/// </summary>
public sealed class FileMentionSearchService
{
    /// <summary>Default number of suggestions returned.</summary>
    public const int DefaultLimit = 8;

    private readonly WorkspaceFileIndex _index;
    private readonly FrecencyStore _frecency;

    public FileMentionSearchService(string workspaceRoot, WorkspaceFileIndex? index = null, FrecencyStore? frecency = null)
    {
        _index = index ?? new WorkspaceFileIndex(workspaceRoot);
        _frecency = frecency ?? new FrecencyStore();
    }

    public FileMentionSearchService(WorkspaceFileIndex index, FrecencyStore? frecency = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _frecency = frecency ?? new FrecencyStore();
    }

    /// <summary>Workspace root that mentions resolve against.</summary>
    public string WorkspaceRoot => _index.Root;

    /// <summary>The underlying file listing (exposed for invalidation).</summary>
    public WorkspaceFileIndex Index => _index;

    /// <summary>Recent-selection tracker used for ranking.</summary>
    public FrecencyStore Frecency => _frecency;

    /// <summary>
    /// Rank workspace entries against <paramref name="query"/>. An empty query returns the most
    /// recently selected paths first, then the shallowest entries.
    /// </summary>
    public IReadOnlyList<FileMentionSuggestion> Search(string? query, int limit = DefaultLimit)
    {
        string normalizedQuery = FileMentionSyntax.NormalizeSeparators(query ?? string.Empty);
        limit = Math.Max(1, limit);

        var scored = new List<FileMentionSuggestion>();
        foreach (var entry in _index.GetEntries())
        {
            if (!FuzzyMatcher.TryMatchPath(entry.RelativePath, normalizedQuery, out int score))
            {
                continue;
            }

            score += _frecency.GetBonus(entry.RelativePath);
            if (entry.IsDirectory)
            {
                // Directories are useful for drilling in but are rarely the final target.
                score -= 4;
            }

            scored.Add(new FileMentionSuggestion(entry.RelativePath, entry.IsDirectory, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.RelativePath.Length)
            .ThenBy(s => s.RelativePath, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>Record that the user accepted <paramref name="relativePath"/> from the picker.</summary>
    public void RecordSelection(string relativePath) => _frecency.Record(relativePath);
}
