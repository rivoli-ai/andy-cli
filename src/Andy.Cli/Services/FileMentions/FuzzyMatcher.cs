using System;

namespace Andy.Cli.Services.FileMentions;

/// <summary>
/// Small subsequence-based fuzzy matcher used to rank file paths for @-mentions.
/// A candidate matches when every character of the query appears in order; the score rewards
/// consecutive runs, matches at word/segment boundaries and exact-case matches, and penalises
/// long gaps and long candidates.
/// </summary>
public static class FuzzyMatcher
{
    private const int ConsecutiveBonus = 16;
    private const int BoundaryBonus = 10;
    private const int CaseMatchBonus = 3;
    private const int FirstCharBonus = 12;
    private const int GapPenalty = 1;
    private const int MaxGapPenalty = 24;

    /// <summary>
    /// Try to match <paramref name="query"/> against <paramref name="candidate"/>.
    /// An empty query matches with a score of zero.
    /// </summary>
    public static bool TryMatch(string candidate, string query, out int score)
    {
        score = 0;
        if (candidate is null)
        {
            return false;
        }
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        int candidateIndex = 0;
        int gapTotal = 0;
        int previousMatch = -2;

        for (int q = 0; q < query.Length; q++)
        {
            char wanted = query[q];
            int found = IndexOfIgnoreCase(candidate, wanted, candidateIndex);
            if (found < 0)
            {
                score = 0;
                return false;
            }

            if (found == previousMatch + 1)
            {
                score += ConsecutiveBonus;
            }
            else
            {
                int gap = found - candidateIndex;
                gapTotal += Math.Min(gap, MaxGapPenalty);
            }

            if (found == 0)
            {
                score += FirstCharBonus;
            }
            else if (IsBoundary(candidate, found))
            {
                score += BoundaryBonus;
            }

            if (candidate[found] == wanted)
            {
                score += CaseMatchBonus;
            }

            previousMatch = found;
            candidateIndex = found + 1;
        }

        score -= gapTotal * GapPenalty;
        // Prefer shorter candidates when everything else is equal.
        score -= candidate.Length / 8;
        return true;
    }

    /// <summary>
    /// Score a workspace-relative path against a query. Matches confined to the file name are
    /// preferred over matches that only line up across directory segments, which is what users
    /// expect when they type a bare file name.
    /// </summary>
    public static bool TryMatchPath(string relativePath, string query, out int score)
    {
        score = 0;
        if (relativePath is null)
        {
            return false;
        }
        if (string.IsNullOrEmpty(query))
        {
            // Shallow paths first when there is nothing to match on.
            score = -CountSegments(relativePath);
            return true;
        }

        bool matchedFull = TryMatch(relativePath, query, out int fullScore);

        int slash = relativePath.LastIndexOf('/');
        string fileName = slash >= 0 ? relativePath.Substring(slash + 1) : relativePath;
        int nameScore = int.MinValue;
        bool matchedName = !query.Contains('/') && TryMatch(fileName, query, out nameScore);
        nameScore = matchedName ? nameScore + 25 : int.MinValue;

        if (!matchedFull && !matchedName)
        {
            return false;
        }

        score = Math.Max(matchedFull ? fullScore : int.MinValue, nameScore);
        score -= CountSegments(relativePath);
        return true;
    }

    private static int CountSegments(string path)
    {
        int count = 0;
        foreach (var c in path)
        {
            if (c == '/')
            {
                count++;
            }
        }
        return count;
    }

    private static int IndexOfIgnoreCase(string haystack, char needle, int startIndex)
    {
        char lower = char.ToLowerInvariant(needle);
        for (int i = startIndex; i < haystack.Length; i++)
        {
            if (char.ToLowerInvariant(haystack[i]) == lower)
            {
                return i;
            }
        }
        return -1;
    }

    private static bool IsBoundary(string candidate, int index)
    {
        char previous = candidate[index - 1];
        if (previous is '/' or '\\' or '_' or '-' or '.' or ' ')
        {
            return true;
        }
        return char.IsLower(previous) && char.IsUpper(candidate[index]);
    }
}
