using System;
using Andy.Cli.Services.TextWrapping;

namespace Andy.Cli.Examples
{
    /// <summary>
    /// Simple test to verify Knuth-Plass text wrapping functionality.
    /// </summary>
    public class TextWrappingTest
    {
        public static void RunQuickTest()
        {
            Console.WriteLine("=== Quick Knuth-Plass Test ===\n");

            // Create wrapper
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

            // Test 3: Line measurement
            var text3 = "This is a longer text that will be measured to see if the line count prediction works correctly.";
            var width3 = 30;

            Console.WriteLine($"Test 3 - Line measurement:");
            Console.WriteLine($"Text: '{text3}'");
            Console.WriteLine($"Width: {width3}");
            
            var measuredLines = wrapper.MeasureLineCount(text3, width3);
            var actualResult = wrapper.WrapText(text3, width3);
            
            Console.WriteLine($"Measured lines: {measuredLines}");
            Console.WriteLine($"Actual lines: {actualResult.LineCount}");
            Console.WriteLine($"Match: {measuredLines == actualResult.LineCount}\n");

            Console.WriteLine("=== Test Complete ===");
        }
    }
}
