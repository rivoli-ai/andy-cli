using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Diagnostic test to trace where text duplication occurs in the streaming pipeline
/// </summary>
public class StreamingDiagnosticTest
{
    private readonly ITestOutputHelper _output;

    public StreamingDiagnosticTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TraceStreamingPipeline()
    {
        // This test traces the flow of text through the streaming pipeline
        var testText = "I'm ready to help. I see we are in /Users/samibengrine/Devel/rivoli-ai/andy-cli/ directory.";

        _output.WriteLine("=== STREAMING PIPELINE TRACE ===");
        _output.WriteLine($"Original text: {testText}");
        _output.WriteLine($"Length: {testText.Length}");

        // Step 1: Simulate chunking
        var chunks = SimulateChunking(testText, 30);
        _output.WriteLine($"\nStep 1: Chunking ({chunks.Count} chunks):");
        for (int i = 0; i < chunks.Count; i++)
        {
            _output.WriteLine($"  Chunk {i}: [{chunks[i]}]");
        }

        // Step 2: Simulate accumulation
        var accumulated = new StringBuilder();
        _output.WriteLine("\nStep 2: Accumulation:");
        foreach (var chunk in chunks)
        {
            accumulated.Append(chunk);
            _output.WriteLine($"  After chunk: Length={accumulated.Length}");
        }
        _output.WriteLine($"  Final accumulated: [{accumulated}]");

        // Step 3: Check for duplication
        var finalText = accumulated.ToString();
        _output.WriteLine($"\nStep 3: Duplication Check:");
        _output.WriteLine($"  Final length: {finalText.Length}");
        _output.WriteLine($"  Original length: {testText.Length}");
        _output.WriteLine($"  Match: {finalText == testText}");

        // Check for specific duplication patterns
        CheckForDuplication(finalText);
    }

    [Fact]
    public void DetectDuplicationPattern()
    {
        // Test with the actual duplicated output from the user
        var duplicatedText = @"I'm ready to help.  I see we are in /Users/samibengrine/Devel/rivoli-ai/andy-cli/ directory.
I'm ready to help. I see we are in /Users/samibengrine/Devel/rivoli-ai/andy-cli/ directory.";

        _output.WriteLine("=== DUPLICATION PATTERN ANALYSIS ===");

        // Split by newlines
        var lines = duplicatedText.Split('\n');
        _output.WriteLine($"Lines found: {lines.Length}");

        for (int i = 0; i < lines.Length; i++)
        {
            _output.WriteLine($"Line {i}: [{lines[i]}]");
        }

        // Check for exact duplicates
        if (lines.Length >= 2)
        {
            var similarity = CalculateSimilarity(lines[0], lines[1]);
            _output.WriteLine($"\nSimilarity between line 0 and 1: {similarity:P}");

            // Check character differences
            _output.WriteLine("\nCharacter differences:");
            var minLen = Math.Min(lines[0].Length, lines[1].Length);
            for (int i = 0; i < minLen; i++)
            {
                if (lines[0][i] != lines[1][i])
                {
                    _output.WriteLine($"  Position {i}: '{lines[0][i]}' vs '{lines[1][i]}'");
                }
            }
        }

        // Look for patterns
        _output.WriteLine("\nPattern Analysis:");
        var firstSentence = "I'm ready to help.";
        var occurrences = CountOccurrences(duplicatedText, firstSentence);
        _output.WriteLine($"  Occurrences of '{firstSentence}': {occurrences}");

        // Analyze positions
        var positions = FindAllOccurrences(duplicatedText, firstSentence);
        _output.WriteLine($"  Positions: {string.Join(", ", positions)}");

        if (positions.Count >= 2)
        {
            _output.WriteLine($"  Distance between occurrences: {positions[1] - positions[0]} characters");
        }
    }

    [Fact]
    public void TestCleanResponseTextEffect()
    {
        // Test if CleanResponseText might be causing duplication
        var parser = new QwenResponseParser(
            new JsonRepairService(),
            new StreamingToolCallAccumulator(new JsonRepairService(), null),
            null);

        var testCases = new[]
        {
            "I'm ready to help. I see we are in the directory.",
            "No need to use tools. I'm ready to help.",
            "Let me check that. I'm ready to help.",
            "I'm ready to help.\nI'm ready to help." // Already duplicated
        };

        _output.WriteLine("=== CLEAN RESPONSE TEXT EFFECT ===");

        foreach (var test in testCases)
        {
            var cleaned = parser.CleanResponseText(test);
            _output.WriteLine($"\nOriginal: [{test}]");
            _output.WriteLine($"Cleaned:  [{cleaned}]");
            _output.WriteLine($"Changed:  {test != cleaned}");
        }
    }

    private List<string> SimulateChunking(string text, int chunkSize)
    {
        var chunks = new List<string>();
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            chunks.Add(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
        }
        return chunks;
    }

    private void CheckForDuplication(string text)
    {
        // Check if text contains itself
        var halfLength = text.Length / 2;
        if (text.Length > 10)
        {
            var firstHalf = text.Substring(0, halfLength);
            var secondHalf = text.Substring(halfLength);

            _output.WriteLine($"\nDuplication analysis:");
            _output.WriteLine($"  First half:  [{firstHalf}]");
            _output.WriteLine($"  Second half: [{secondHalf}]");

            var similarity = CalculateSimilarity(firstHalf, secondHalf);
            _output.WriteLine($"  Similarity: {similarity:P}");
        }
    }

    private double CalculateSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0;

        var longer = a.Length > b.Length ? a : b;
        var shorter = a.Length > b.Length ? b : a;

        if (longer.Length == 0)
            return 1.0;

        var editDistance = ComputeLevenshteinDistance(a, b);
        return (longer.Length - editDistance) / (double)longer.Length;
    }

    private int ComputeLevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];

        if (n == 0) return m;
        if (m == 0) return n;

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (b[j - 1] == a[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }

    private int CountOccurrences(string text, string phrase)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(phrase, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += phrase.Length;
        }
        return count;
    }

    private List<int> FindAllOccurrences(string text, string phrase)
    {
        var positions = new List<int>();
        int index = 0;
        while ((index = text.IndexOf(phrase, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            positions.Add(index);
            index += phrase.Length;
        }
        return positions;
    }
}