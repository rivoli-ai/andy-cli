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

