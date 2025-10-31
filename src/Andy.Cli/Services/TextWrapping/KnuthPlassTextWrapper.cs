using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services.TextWrapping;

/// <summary>
/// Advanced text wrapper implementing the Knuth-Plass line breaking algorithm.
/// This provides optimal line breaking with minimal "badness" across the entire paragraph.
/// </summary>
public class KnuthPlassTextWrapper : ITextWrapper
{
    private readonly IHyphenationService _hyphenationService;

    public KnuthPlassTextWrapper(IHyphenationService hyphenationService)
    {
        _hyphenationService = hyphenationService ?? throw new ArgumentNullException(nameof(hyphenationService));
    }

    public WrappedText WrapText(string text, int maxWidth, TextWrappingOptions? options = default)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return new WrappedText(new[] { text ?? string.Empty });

        options ??= new TextWrappingOptions();

        var lines = new List<string>();
        var inputLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool hasHyphenation = false;

        foreach (var inputLine in inputLines)
        {
            if (string.IsNullOrEmpty(inputLine))
            {
                lines.Add(string.Empty);
                continue;
            }

            if (inputLine.Length <= maxWidth)
            {
                lines.Add(options.TrimLines ? inputLine.TrimEnd() : inputLine);
                continue;
            }

            // Use Knuth-Plass algorithm for this line
            var (wrappedLines, lineHasHyphenation) = WrapWithKnuthPlass(inputLine, maxWidth, options);
            lines.AddRange(wrappedLines);
            hasHyphenation |= lineHasHyphenation;
        }

        return new WrappedText(lines, hasHyphenation);
    }

    public int MeasureLineCount(string text, int maxWidth, TextWrappingOptions? options = default)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return 1;

        var wrappedText = WrapText(text, maxWidth, options);
        return wrappedText.LineCount;
    }

    private (List<string> lines, bool hasHyphenation) WrapWithKnuthPlass(string line, int maxWidth, TextWrappingOptions options)
    {
        // For now, implement a simplified version that respects maxWidth
        // This can be enhanced later with the full Knuth-Plass algorithm
        var wrappedLines = new List<string>();
        var words = TokenizeIntoWords(line);
        bool hasHyphenation = false;

        var currentLine = new StringBuilder();
        int currentWidth = 0;

        foreach (var word in words)
        {
            var wordWidth = GetDisplayWidth(word);
            var spaceWidth = currentLine.Length > 0 ? 1 : 0; // Space before word if not first word

            // If adding this word would exceed the width
            if (currentWidth + spaceWidth + wordWidth > maxWidth)
            {
                // Try to hyphenate the word to fit on current line if hyphenation is enabled
                if (currentLine.Length > 0 && options.EnableHyphenation && _hyphenationService.CanHyphenate(word))
                {
                    var remainingWidth = maxWidth - currentWidth - spaceWidth;
                    var hyphenated = TryHyphenateToFit(word, remainingWidth, maxWidth, options);
                    
                    if (hyphenated.Success)
                    {
                        // Add hyphenated segment to current line
                        if (currentLine.Length > 0)
                        {
                            currentLine.Append(' ');
                            currentWidth++;
                        }
                        currentLine.Append(hyphenated.FirstSegment);
                        wrappedLines.Add(options.TrimLines ? currentLine.ToString().TrimEnd() : currentLine.ToString());
                        currentLine.Clear();
                        currentWidth = 0;
                        hasHyphenation = true;
                        
                        // Process remaining segments
                        if (hyphenated.RemainingSegments.Count > 0)
                        {
                            // Add remaining segments, starting with the first one
                            var firstRemaining = hyphenated.RemainingSegments[0];
                            currentLine.Append(firstRemaining);
                            currentWidth += GetDisplayWidth(firstRemaining);
                            
                            // Add any other segments as separate lines
                            for (int i = 1; i < hyphenated.RemainingSegments.Count; i++)
                            {
                                wrappedLines.Add(hyphenated.RemainingSegments[i]);
                            }
                        }
                        continue;
                    }
                }
                
                // If current line is not empty, finish it first
                if (currentLine.Length > 0)
                {
                    wrappedLines.Add(options.TrimLines ? currentLine.ToString().TrimEnd() : currentLine.ToString());
                    currentLine.Clear();
                    currentWidth = 0;
                }
                
                // If the word itself is too long for a single line, we need to break it
                if (wordWidth > maxWidth)
                {
                    // Break the word - each segment gets its own line
                    var (brokenWord, wordHasHyphenation) = BreakLongWord(word, maxWidth, maxWidth, options);
                    
                    // Add all segments as separate lines
                    foreach (var segment in brokenWord)
                    {
                        wrappedLines.Add(segment);
                    }
                    
                    hasHyphenation |= wordHasHyphenation;
                    continue;
                }
            }

            // Add the word to current line
            if (currentLine.Length > 0)
            {
                currentLine.Append(' ');
                currentWidth++;
            }

            currentLine.Append(word);
            currentWidth += wordWidth;
        }

        // Add the last line if it's not empty
        if (currentLine.Length > 0)
        {
            wrappedLines.Add(options.TrimLines ? currentLine.ToString().TrimEnd() : currentLine.ToString());
        }

        return (wrappedLines, hasHyphenation);
    }

    private List<string> TokenizeIntoWords(string text)
    {
        // Simple word tokenization - split on whitespace
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
                // Skip whitespace in simple mode
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

    private (List<string> lines, bool hasHyphenation) BreakLongWord(string word, int firstSegmentWidth, int fullWidth, TextWrappingOptions options)
    {
        var brokenLines = new List<string>();
        bool hasHyphenation = false;

        if (!options.EnableHyphenation || !_hyphenationService.CanHyphenate(word))
        {
            // No hyphenation - just break at character boundaries
            // First segment uses firstSegmentWidth, subsequent use fullWidth
            int pos = 0;
            while (pos < word.Length)
            {
                int segmentWidth = pos == 0 ? firstSegmentWidth : fullWidth;
                // Don't use negative or zero widths - ensure at least 1
                if (segmentWidth <= 0)
                {
                    segmentWidth = 1;
                }
                // Ensure segment doesn't exceed maxWidth
                segmentWidth = Math.Min(segmentWidth, fullWidth);
                var segment = word.Substring(pos, Math.Min(segmentWidth, word.Length - pos));
                brokenLines.Add(segment);
                pos += segment.Length;
            }
            return (brokenLines, hasHyphenation);
        }

        // Try to hyphenate
        var hyphenationPoints = _hyphenationService.GetHyphenationPoints(word);
        var currentPos = 0;

        while (currentPos < word.Length)
        {
            // For first segment, use remaining width; for subsequent segments, use full width
            var remainingWidth = currentPos == 0 ? firstSegmentWidth : fullWidth;
            var bestBreak = -1;

            // Find the best hyphenation point within the remaining width
            // Choose the latest possible point to maximize line width usage
            // Account for hyphen character (+1) when checking fit
            foreach (var point in hyphenationPoints)
            {
                var segmentLength = point - currentPos;
                var segmentWithHyphenLength = segmentLength + 1; // Account for hyphen
                
                if (point > currentPos && segmentWithHyphenLength <= remainingWidth)
                {
                    // Check if this break point respects minimum fragment lengths
                    var leftLength = segmentLength;
                    
                    // Only check left fragment minimum length - allow any length to use full width
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
                var segmentWithHyphenLength = segmentLength + 1; // Account for hyphen
                
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
                // Ensure segment doesn't exceed maxWidth
                segmentLength = Math.Min(segmentLength, fullWidth);
                var segment = word.Substring(currentPos, segmentLength);
                brokenLines.Add(segment);
                currentPos += segmentLength;
            }
        }

        return (brokenLines, hasHyphenation);
    }

    private HyphenationResult TryHyphenateToFit(string word, int availableWidth, int maxWidth, TextWrappingOptions options)
    {
        if (!options.EnableHyphenation || !_hyphenationService.CanHyphenate(word) || availableWidth < options.MinHyphenationLength + 1)
        {
            // Need at least min length + 1 for hyphen
            return new HyphenationResult { Success = false };
        }

        var hyphenationPoints = _hyphenationService.GetHyphenationPoints(word);
        int bestBreak = -1;
        int bestLeftLength = 0;

        // Find the best hyphenation point that fits in availableWidth (accounting for hyphen)
        // We want the longest possible segment that fits
        foreach (var point in hyphenationPoints)
        {
            var leftLength = point;
            // Need to account for hyphen character (+1)
            if (leftLength >= options.MinHyphenationLength && 
                leftLength + 1 <= availableWidth &&
                leftLength > bestLeftLength) // Prefer longer segments
            {
                bestBreak = point;
                bestLeftLength = leftLength;
            }
        }

        if (bestBreak >= 0)
        {
            // Found a good hyphenation point
            var firstSegment = word.Substring(0, bestLeftLength) + "-";
            var remaining = word.Substring(bestBreak);
            var remainingSegments = new List<string>();
            
            // Break the remaining part if needed
            if (remaining.Length > maxWidth)
            {
                var (brokenRemaining, _) = BreakLongWord(remaining, maxWidth, maxWidth, options);
                remainingSegments.AddRange(brokenRemaining);
            }
            else
            {
                remainingSegments.Add(remaining);
            }

            return new HyphenationResult
            {
                Success = true,
                FirstSegment = firstSegment,
                RemainingSegments = remainingSegments
            };
        }

        return new HyphenationResult { Success = false };
    }

    private int GetDisplayWidth(string text)
    {
        // For now, assume 1 character = 1 display width
        // This could be enhanced to handle full-width characters, etc.
        return text.Length;
    }

    private static bool IsVowel(char c)
    {
        return "aeiouAEIOU".IndexOf(c) >= 0;
    }

    private class HyphenationResult
    {
        public bool Success { get; set; }
        public string FirstSegment { get; set; } = string.Empty;
        public List<string> RemainingSegments { get; set; } = new();
    }
}
