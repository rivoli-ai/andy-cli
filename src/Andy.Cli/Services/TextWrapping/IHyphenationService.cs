using System;
using System.Collections.Generic;

namespace Andy.Cli.Services.TextWrapping;

/// <summary>
/// Interface for hyphenation services that can determine where words can be broken.
/// </summary>
public interface IHyphenationService
{
    /// <summary>
    /// Gets the hyphenation points for a word (character positions where hyphenation is allowed).
    /// </summary>
    /// <param name="word">The word to hyphenate</param>
    /// <returns>List of character positions where hyphenation is allowed</returns>
    IReadOnlyList<int> GetHyphenationPoints(string word);

    /// <summary>
    /// Determines whether a word can be hyphenated.
    /// </summary>
    /// <param name="word">The word to check</param>
    /// <returns>True if the word can be hyphenated</returns>
    bool CanHyphenate(string word);

    /// <summary>
    /// Gets the language code for this hyphenation service.
    /// </summary>
    string LanguageCode { get; }
}

/// <summary>
/// Simple hyphenation service that uses basic rules for English.
/// </summary>
public class SimpleHyphenationService : IHyphenationService
{
    public string LanguageCode => "en";

    public IReadOnlyList<int> GetHyphenationPoints(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 4)
            return Array.Empty<int>();

        var points = new List<int>();
        
        // Simple English hyphenation rules
        for (int i = 2; i < word.Length - 2; i++)
        {
            if (CanHyphenateAt(word, i))
            {
                points.Add(i);
            }
        }

        return points;
    }

    public bool CanHyphenate(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 4)
            return false;

        // Check if there's at least one valid hyphenation point
        for (int i = 2; i < word.Length - 2; i++)
        {
            if (CanHyphenateAt(word, i))
                return true;
        }

        return false;
    }

    private static bool CanHyphenateAt(string word, int position)
    {
        // Basic English hyphenation rules
        char prev = word[position - 1];
        char curr = word[position];
        char next = word[position + 1];

        // Don't break after single letters
        if (position == 1)
            return false;

        // Don't break before single letters
        if (position == word.Length - 2)
            return false;

        // Break after vowels followed by consonants
        if (IsVowel(prev) && IsConsonant(curr))
            return true;

        // Break between double consonants
        if (prev == curr && IsConsonant(curr))
            return true;

        // Break after consonant-vowel-consonant patterns
        if (position >= 2 && position < word.Length - 2)
        {
            char prev2 = word[position - 2];
            if (IsConsonant(prev2) && IsVowel(prev) && IsConsonant(curr) && IsVowel(next))
                return true;
        }

        return false;
    }

    private static bool IsVowel(char c)
    {
        return "aeiouAEIOU".IndexOf(c) >= 0;
    }

    private static bool IsConsonant(char c)
    {
        return char.IsLetter(c) && !IsVowel(c);
    }
}

/// <summary>
/// Advanced hyphenation service using TeX hyphenation patterns.
/// </summary>
public class TeXHyphenationService : IHyphenationService
{
    private readonly Dictionary<string, int[]> _patterns;
    private readonly HashSet<string> _exceptions;

    public string LanguageCode => "en";

    public TeXHyphenationService()
    {
        _patterns = LoadHyphenationPatterns();
        _exceptions = LoadHyphenationExceptions();
    }

    public IReadOnlyList<int> GetHyphenationPoints(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 4)
            return Array.Empty<int>();

        // Check exceptions first
        if (_exceptions.Contains(word.ToLowerInvariant()))
        {
            return GetExceptionHyphenationPoints(word);
        }

        // Apply hyphenation patterns
        var points = ApplyHyphenationPatterns(word.ToLowerInvariant());
        
        // Filter out points that are too close to the beginning or end
        var filteredPoints = new List<int>();
        foreach (var point in points)
        {
            if (point >= 2 && point <= word.Length - 2)
            {
                filteredPoints.Add(point);
            }
        }

        return filteredPoints;
    }

    public bool CanHyphenate(string word)
    {
        return GetHyphenationPoints(word).Count > 0;
    }

    private IReadOnlyList<int> GetExceptionHyphenationPoints(string word)
    {
        // Handle hyphenation exceptions (words with manual hyphenation points)
        var points = new List<int>();
        var lowerWord = word.ToLowerInvariant();
        
        // Simple exception handling - look for common patterns
        if (lowerWord.Contains("tion") && lowerWord.Length > 6)
        {
            int pos = lowerWord.IndexOf("tion");
            if (pos >= 2)
                points.Add(pos);
        }

        return points;
    }

    private IReadOnlyList<int> ApplyHyphenationPatterns(string word)
    {
        var points = new List<int>();
        
        // Apply TeX hyphenation algorithm
        var scores = new int[word.Length + 1];
        
        // Apply patterns
        foreach (var pattern in _patterns)
        {
            var patternText = pattern.Key;
            var patternScores = pattern.Value;
            
            int pos = 0;
            while ((pos = word.IndexOf(patternText, pos)) >= 0)
            {
                for (int i = 0; i < patternScores.Length && pos + i < scores.Length; i++)
                {
                    scores[pos + i] = Math.Max(scores[pos + i], patternScores[i]);
                }
                pos++;
            }
        }

        // Find hyphenation points (odd scores)
        for (int i = 0; i < scores.Length; i++)
        {
            if (scores[i] % 2 == 1)
            {
                points.Add(i);
            }
        }

        return points;
    }

    private Dictionary<string, int[]> LoadHyphenationPatterns()
    {
        // Load basic English hyphenation patterns
        // This is a simplified version - a full implementation would load from files
        var patterns = new Dictionary<string, int[]>
        {
            // Common English patterns
            { "tion", new[] { 0, 0, 0, 1 } },
            { "sion", new[] { 0, 0, 0, 1 } },
            { "able", new[] { 0, 0, 1, 0 } },
            { "ible", new[] { 0, 0, 1, 0 } },
            { "ment", new[] { 0, 0, 1, 0 } },
            { "ness", new[] { 0, 0, 1, 0 } },
            { "less", new[] { 0, 0, 1, 0 } },
            { "ful", new[] { 0, 0, 1 } },
            { "ing", new[] { 0, 0, 1 } },
            { "ed", new[] { 0, 1 } },
            { "er", new[] { 0, 1 } },
            { "ly", new[] { 0, 1 } },
            
            // Vowel-consonant patterns
            { "a", new[] { 0, 1 } },
            { "e", new[] { 0, 1 } },
            { "i", new[] { 0, 1 } },
            { "o", new[] { 0, 1 } },
            { "u", new[] { 0, 1 } },
            
            // Consonant patterns
            { "bl", new[] { 0, 1 } },
            { "br", new[] { 0, 1 } },
            { "cl", new[] { 0, 1 } },
            { "cr", new[] { 0, 1 } },
            { "dr", new[] { 0, 1 } },
            { "fl", new[] { 0, 1 } },
            { "fr", new[] { 0, 1 } },
            { "gl", new[] { 0, 1 } },
            { "gr", new[] { 0, 1 } },
            { "pl", new[] { 0, 1 } },
            { "pr", new[] { 0, 1 } },
            { "sl", new[] { 0, 1 } },
            { "st", new[] { 0, 1 } },
            { "tr", new[] { 0, 1 } },
        };

        return patterns;
    }

    private HashSet<string> LoadHyphenationExceptions()
    {
        // Common words that should not be hyphenated or have special rules
        return new HashSet<string>
        {
            "the", "and", "for", "are", "but", "not", "you", "all", "can", "had", "her", "was", "one", "our", "out", "day", "get", "has", "him", "his", "how", "its", "may", "new", "now", "old", "see", "two", "way", "who", "boy", "did", "man", "men", "put", "say", "she", "too", "use"
        };
    }
}
