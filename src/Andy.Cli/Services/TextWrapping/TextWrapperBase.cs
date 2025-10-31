using System.Text;

namespace Andy.Cli.Services.TextWrapping;

/// <summary>
/// Base class for text wrappers that provides common functionality.
/// </summary>
public abstract class TextWrapperBase : ITextWrapper
{
    protected readonly IHyphenationService HyphenationService;

    protected TextWrapperBase(IHyphenationService hyphenationService)
    {
        HyphenationService = hyphenationService ?? throw new ArgumentNullException(nameof(hyphenationService));
    }

    public abstract WrappedText WrapText(string text, int maxWidth, TextWrappingOptions? options = default);

    public int MeasureLineCount(string text, int maxWidth, TextWrappingOptions? options = default)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return 1;

        var wrappedText = WrapText(text, maxWidth, options);
        return wrappedText.LineCount;
    }

    /// <summary>
    /// Tokenizes text into words by splitting on whitespace.
    /// </summary>
    protected List<string> TokenizeIntoWords(string text)
    {
        var words = new List<string>();
        var currentWord = new StringBuilder();

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (currentWord.Length > 0)
                {
                    words.Add(currentWord.ToString());
                    currentWord.Clear();
                }
            }
            else
            {
                currentWord.Append(c);
            }
        }

        if (currentWord.Length > 0)
        {
            words.Add(currentWord.ToString());
        }

        return words;
    }

    /// <summary>
    /// Breaks a long word into segments, using hyphenation when possible.
    /// </summary>
    protected (List<string> lines, bool hasHyphenation) BreakLongWord(
        string word, 
        int firstSegmentWidth, 
        int fullWidth, 
        TextWrappingOptions options)
    {
        var brokenLines = new List<string>();
        bool hasHyphenation = false;

        if (!options.EnableHyphenation || !HyphenationService.CanHyphenate(word))
        {
            // No hyphenation - just break at character boundaries
            int pos = 0;
            while (pos < word.Length)
            {
                int segmentWidth = pos == 0 ? firstSegmentWidth : fullWidth;
                if (segmentWidth <= 0)
                {
                    segmentWidth = 1;
                }
                segmentWidth = Math.Min(segmentWidth, fullWidth);
                var segment = word.Substring(pos, Math.Min(segmentWidth, word.Length - pos));
                brokenLines.Add(segment);
                pos += segment.Length;
            }
            return (brokenLines, hasHyphenation);
        }

        // Try to hyphenate
        var hyphenationPoints = HyphenationService.GetHyphenationPoints(word);
        var currentPos = 0;

        while (currentPos < word.Length)
        {
            var remainingWidth = currentPos == 0 ? firstSegmentWidth : fullWidth;
            var bestBreak = -1;

            // Find the best hyphenation point within the remaining width
            // Account for hyphen character (+1) when checking fit
            foreach (var point in hyphenationPoints)
            {
                var segmentLength = point - currentPos;
                var segmentWithHyphenLength = segmentLength + 1; // Account for hyphen
                
                if (point > currentPos && segmentWithHyphenLength <= remainingWidth)
                {
                    var leftLength = segmentLength;
                    
                    if (leftLength >= options.MinHyphenationLength &&
                        point > bestBreak) // Prefer later break points
                    {
                        bestBreak = point;
                    }
                }
            }

            if (bestBreak >= 0)
            {
                // Use hyphenation - check if adding hyphen would exceed width
                var segmentLength = bestBreak - currentPos;
                var segmentWithHyphenLength = segmentLength + 1;
                
                // Check if segment with hyphen fits
                if (segmentWithHyphenLength <= remainingWidth)
                {
                    // Use hyphenation
                    var segment = word.Substring(currentPos, segmentLength) + "-";
                    brokenLines.Add(segment);
                    currentPos = bestBreak;
                    hasHyphenation = true;
                }
                else
                {
                    // Hyphen would exceed width, fall back to character breaking
                    var segmentLength2 = Math.Max(1, Math.Min(remainingWidth, word.Length - currentPos));
                    segmentLength2 = Math.Min(segmentLength2, fullWidth);
                    var segment = word.Substring(currentPos, segmentLength2);
                    brokenLines.Add(segment);
                    currentPos += segmentLength2;
                }
            }
            else
            {
                // Fall back to character boundary breaking
                var segmentLength = Math.Max(1, Math.Min(remainingWidth, word.Length - currentPos));
                segmentLength = Math.Min(segmentLength, fullWidth);
                var segment = word.Substring(currentPos, segmentLength);
                brokenLines.Add(segment);
                currentPos += segmentLength;
            }
        }

        return (brokenLines, hasHyphenation);
    }

    /// <summary>
    /// Gets the display width of text (for now, assumes 1 character = 1 width).
    /// </summary>
    protected int GetDisplayWidth(string text)
    {
        return text.Length;
    }
}
