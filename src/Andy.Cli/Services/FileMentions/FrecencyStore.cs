using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Services.FileMentions;

/// <summary>
/// Session-scoped frecency tracker: remembers which paths the user has picked from the
/// @-mention menu and how recently, so equally good fuzzy matches are ordered by past use.
///
/// Deliberately in-memory only. Persisting a list of paths the user attached to prompts would
/// create an on-disk record of what they shared with the model, which is not worth the ranking
/// gain for a first slice.
/// </summary>
public sealed class FrecencyStore
{
    private const int MaxTrackedPaths = 200;
    private const int MaxRecencyBonus = 40;
    private const int RecencyDecay = 4;
    private const int UseBonus = 6;
    private const int MaxUseBonus = 30;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _useCounts = new(StringComparer.Ordinal);
    private readonly List<string> _mostRecentFirst = new();

    /// <summary>Record that the user selected <paramref name="relativePath"/>.</summary>
    public void Record(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        lock (_gate)
        {
            _useCounts[relativePath] = _useCounts.TryGetValue(relativePath, out var count) ? count + 1 : 1;
            _mostRecentFirst.RemoveAll(p => string.Equals(p, relativePath, StringComparison.Ordinal));
            _mostRecentFirst.Insert(0, relativePath);
            if (_mostRecentFirst.Count > MaxTrackedPaths)
            {
                var dropped = _mostRecentFirst[^1];
                _mostRecentFirst.RemoveAt(_mostRecentFirst.Count - 1);
                _useCounts.Remove(dropped);
            }
        }
    }

    /// <summary>Ranking bonus for a path: larger for more recent and more frequent selections.</summary>
    public int GetBonus(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return 0;
        }

        lock (_gate)
        {
            int index = _mostRecentFirst.FindIndex(p => string.Equals(p, relativePath, StringComparison.Ordinal));
            if (index < 0)
            {
                return 0;
            }

            int recency = Math.Max(0, MaxRecencyBonus - (index * RecencyDecay));
            int uses = Math.Min(MaxUseBonus, _useCounts.GetValueOrDefault(relativePath) * UseBonus);
            return recency + uses;
        }
    }

    /// <summary>Paths in most-recently-selected order.</summary>
    public IReadOnlyList<string> RecentPaths
    {
        get { lock (_gate) { return _mostRecentFirst.ToList(); } }
    }

    /// <summary>Forget everything recorded so far.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _useCounts.Clear();
            _mostRecentFirst.Clear();
        }
    }
}
