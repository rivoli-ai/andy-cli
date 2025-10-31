using System;
using Andy.Cli.Services.TextWrapping;

namespace Andy.Cli.Examples
{
    /// <summary>
    /// Demonstrates Knuth-Plass text wrapping functionality with console output.
    /// </summary>
    public static class TextWrappingDemo
    {
        public static void ShowKnuthPlassExample()
        {
            Console.WriteLine("=== Knuth-Plass Text Wrapper Demo ===\n");

            // Create the text wrapper
            var hyphenationService = new SimpleHyphenationService();
            var textWrapper = new KnuthPlassTextWrapper(hyphenationService);

            // Example 1: Long paragraph wrapping
            var longText = "This is a very long paragraph that demonstrates the Knuth-Plass algorithm's ability to find optimal line breaks. The algorithm considers the entire paragraph as a whole and minimizes the 'badness' of the overall layout by balancing line lengths and avoiding awkward breaks.";
            var maxWidth = 50;

            Console.WriteLine($"Example 1 - Long paragraph wrapping:");
            Console.WriteLine($"Original text ({longText.Length} characters):");
            Console.WriteLine($"'{longText}'\n");

            Console.WriteLine($"Wrapped to {maxWidth} characters:");
            Console.WriteLine("┌" + new string('─', maxWidth) + "┐");

            var wrappedResult = textWrapper.WrapText(longText, maxWidth);
            foreach (var line in wrappedResult.Lines)
            {
                Console.WriteLine($"│{line.PadRight(maxWidth)}│");
            }
            Console.WriteLine("└" + new string('─', maxWidth) + "┘");
            Console.WriteLine($"Total lines: {wrappedResult.LineCount}");
            Console.WriteLine($"Max line width: {wrappedResult.MaxLineWidth}");
            Console.WriteLine($"Has hyphenation: {wrappedResult.HasHyphenation}\n");

            // Example 2: Hyphenation demonstration
            var hyphenationText = "supercalifragilisticexpialidocious";
            var narrowWidth = 15;

            Console.WriteLine($"Example 2 - Hyphenation:");
            Console.WriteLine($"Word: '{hyphenationText}'");
            Console.WriteLine($"Width: {narrowWidth} characters");
            Console.WriteLine("┌" + new string('─', narrowWidth) + "┐");

            var hyphenationOptions = new TextWrappingOptions
            {
                EnableHyphenation = true,
                MinHyphenationLength = 3,
                MaxHyphenationLength = 5
            };

            var hyphenatedResult = textWrapper.WrapText(hyphenationText, narrowWidth, hyphenationOptions);
            foreach (var line in hyphenatedResult.Lines)
            {
                Console.WriteLine($"│{line.PadRight(narrowWidth)}│");
            }
            Console.WriteLine("└" + new string('─', narrowWidth) + "┘");
            Console.WriteLine($"Has hyphenation: {hyphenatedResult.HasHyphenation}\n");

            // Example 3: Knuth-Plass wrapper demonstration
            var comparisonText = "The quick brown fox jumps over the lazy dog. This sentence contains every letter of the alphabet and demonstrates different wrapping strategies.";
            var comparisonWidth = 30;

            Console.WriteLine($"Example 3 - Knuth-Plass Algorithm:");
            Console.WriteLine($"Text: '{comparisonText}'");
            Console.WriteLine($"Width: {comparisonWidth} characters\n");

            // Knuth-Plass wrapper
            var knuthResult = textWrapper.WrapText(comparisonText, comparisonWidth);
            Console.WriteLine("Knuth-Plass Optimal Algorithm:");
            Console.WriteLine("┌" + new string('─', comparisonWidth) + "┐");
            foreach (var line in knuthResult.Lines)
            {
                Console.WriteLine($"│{line.PadRight(comparisonWidth)}│");
            }
            Console.WriteLine("└" + new string('─', comparisonWidth) + "┘");
            Console.WriteLine($"Lines: {knuthResult.LineCount}\n");

            // Example 4: Line count measurement accuracy
            var measurementText = "This is a test to measure how many lines will be needed for wrapping. The measurement function should return the same count as the actual wrapping.";
            var measurementWidth = 40;

            var measuredLines = textWrapper.MeasureLineCount(measurementText, measurementWidth);
            var actualWrapped = textWrapper.WrapText(measurementText, measurementWidth);

            Console.WriteLine($"Example 4 - Line count measurement:");
            Console.WriteLine($"Text: '{measurementText}'");
            Console.WriteLine($"Width: {measurementWidth} characters");
            Console.WriteLine($"Measured lines: {measuredLines}");
            Console.WriteLine($"Actual lines: {actualWrapped.LineCount}");
            Console.WriteLine($"✅ Match: {measuredLines == actualWrapped.LineCount}\n");

            Console.WriteLine("=== Demo Complete ===");
        }

        public static void ShowQuickTest()
        {
            Console.WriteLine("=== Quick Knuth-Plass Test ===\n");

            var hyphenationService = new SimpleHyphenationService();
            var wrapper = new KnuthPlassTextWrapper(hyphenationService);

            // Test 1: Basic wrapping
            var text1 = "This is a test of the Knuth-Plass algorithm for optimal text wrapping.";
            var width1 = 25;

            Console.WriteLine($"Test 1 - Basic wrapping:");
            Console.WriteLine($"Text: '{text1}'");
            Console.WriteLine($"Width: {width1}");
            Console.WriteLine("Result:");
            
            var result1 = wrapper.WrapText(text1, width1);
            foreach (var line in result1.Lines)
            {
                Console.WriteLine($"  '{line}'");
            }
            Console.WriteLine($"Lines: {result1.LineCount}\n");

            // Test 2: Hyphenation
            var text2 = "supercalifragilisticexpialidocious";
            var width2 = 12;

            Console.WriteLine($"Test 2 - Hyphenation:");
            Console.WriteLine($"Text: '{text2}'");
            Console.WriteLine($"Width: {width2}");
            Console.WriteLine("Result:");
            
            var options = new TextWrappingOptions { EnableHyphenation = true };
            var result2 = wrapper.WrapText(text2, width2, options);
            foreach (var line in result2.Lines)
            {
                Console.WriteLine($"  '{line}'");
            }
            Console.WriteLine($"Lines: {result2.LineCount}, Hyphenated: {result2.HasHyphenation}\n");

            Console.WriteLine("=== Test Complete ===");
        }
    }
}
