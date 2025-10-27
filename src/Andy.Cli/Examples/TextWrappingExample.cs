using System;
using Andy.Cli.Services.TextWrapping;

namespace Andy.Cli.Examples
{
    /// <summary>
    /// Example demonstrating Knuth-Plass text wrapping functionality.
    /// </summary>
    public class TextWrappingExample
    {
        public static void RunExample()
        {
            Console.WriteLine("=== Knuth-Plass Text Wrapper Example ===\n");

            // Create the text wrapper with hyphenation service
            var hyphenationService = new SimpleHyphenationService();
            var textWrapper = new KnuthPlassTextWrapper(hyphenationService);

            // Example 1: Long paragraph that needs wrapping
            var longText = "This is a very long paragraph that demonstrates the Knuth-Plass algorithm's ability to find optimal line breaks. The algorithm considers the entire paragraph as a whole and minimizes the 'badness' of the overall layout by balancing line lengths and avoiding awkward breaks.";
            var maxWidth = 50;

            Console.WriteLine($"Original text ({longText.Length} characters):");
            Console.WriteLine($"'{longText}'\n");

            Console.WriteLine($"Wrapped to {maxWidth} characters using Knuth-Plass algorithm:");
            Console.WriteLine("=" + new string('=', maxWidth));

            var wrappedResult = textWrapper.WrapText(longText, maxWidth);
            foreach (var line in wrappedResult.Lines)
            {
                Console.WriteLine($"|{line.PadRight(maxWidth)}|");
            }
            Console.WriteLine("=" + new string('=', maxWidth));
            Console.WriteLine($"Total lines: {wrappedResult.LineCount}");
            Console.WriteLine($"Max line width: {wrappedResult.MaxLineWidth}");
            Console.WriteLine($"Has hyphenation: {wrappedResult.HasHyphenation}\n");

            // Example 2: Text with hyphenation
            var hyphenationText = "supercalifragilisticexpialidocious";
            var narrowWidth = 15;

            Console.WriteLine($"Hyphenation example:");
            Console.WriteLine($"Word: '{hyphenationText}'");
            Console.WriteLine($"Width: {narrowWidth} characters");
            Console.WriteLine("=" + new string('=', narrowWidth));

            var hyphenationOptions = new TextWrappingOptions
            {
                EnableHyphenation = true,
                MinHyphenationLength = 3,
                MaxHyphenationLength = 5
            };

            var hyphenatedResult = textWrapper.WrapText(hyphenationText, narrowWidth, hyphenationOptions);
            foreach (var line in hyphenatedResult.Lines)
            {
                Console.WriteLine($"|{line.PadRight(narrowWidth)}|");
            }
            Console.WriteLine("=" + new string('=', narrowWidth));
            Console.WriteLine($"Has hyphenation: {hyphenatedResult.HasHyphenation}\n");

            // Example 3: Comparison with different algorithms
            var simpleWrapper = new SimpleTextWrapper(hyphenationService);
            var comparisonText = "The quick brown fox jumps over the lazy dog. This sentence contains every letter of the alphabet and demonstrates different wrapping strategies.";
            var comparisonWidth = 30;

            Console.WriteLine("Algorithm Comparison:");
            Console.WriteLine($"Text: '{comparisonText}'");
            Console.WriteLine($"Width: {comparisonWidth} characters\n");

            // Simple wrapper
            var simpleResult = simpleWrapper.WrapText(comparisonText, comparisonWidth);
            Console.WriteLine("Simple Greedy Algorithm:");
            Console.WriteLine("-" + new string('-', comparisonWidth));
            foreach (var line in simpleResult.Lines)
            {
                Console.WriteLine($"|{line.PadRight(comparisonWidth)}|");
            }
            Console.WriteLine("-" + new string('-', comparisonWidth));
            Console.WriteLine($"Lines: {simpleResult.LineCount}\n");

            // Knuth-Plass wrapper
            var knuthResult = textWrapper.WrapText(comparisonText, comparisonWidth);
            Console.WriteLine("Knuth-Plass Optimal Algorithm:");
            Console.WriteLine("-" + new string('-', comparisonWidth));
            foreach (var line in knuthResult.Lines)
            {
                Console.WriteLine($"|{line.PadRight(comparisonWidth)}|");
            }
            Console.WriteLine("-" + new string('-', comparisonWidth));
            Console.WriteLine($"Lines: {knuthResult.LineCount}\n");

            // Example 4: Line count measurement
            var measurementText = "This is a test to measure how many lines will be needed for wrapping. The measurement function should return the same count as the actual wrapping.";
            var measurementWidth = 40;

            var measuredLines = textWrapper.MeasureLineCount(measurementText, measurementWidth);
            var actualWrapped = textWrapper.WrapText(measurementText, measurementWidth);

            Console.WriteLine("Line Count Measurement:");
            Console.WriteLine($"Text: '{measurementText}'");
            Console.WriteLine($"Width: {measurementWidth} characters");
            Console.WriteLine($"Measured lines: {measuredLines}");
            Console.WriteLine($"Actual lines: {actualWrapped.LineCount}");
            Console.WriteLine($"Match: {measuredLines == actualWrapped.LineCount}\n");

            Console.WriteLine("=== Example Complete ===");
        }
    }
}
