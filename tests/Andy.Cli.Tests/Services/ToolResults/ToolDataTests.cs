using System;
using System.Collections.Generic;
using System.Text.Json;
using Andy.Cli.Services.ToolResults;
using Xunit;

namespace Andy.Cli.Tests.Services.ToolResults;

/// <summary>
/// ToolData is the single point where a tool's structured result is interpreted, so these tests
/// pin the shapes Andy.Tools actually returns: generic dictionaries, non-generic dictionaries,
/// JSON elements, and POCOs such as the FileSystemEntry objects list_directory returns.
/// </summary>
public class ToolDataTests
{
    private sealed class Entry
    {
        public string Name { get; set; } = "";
        public bool IsDirectory { get; set; }
        public int LineCount { get; set; }
    }

    [Fact]
    public void ReadsFromGenericDictionary()
    {
        var data = new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "hello" };

        Assert.Equal(0, ToolData.GetInt(data, "exit_code"));
        Assert.Equal("hello", ToolData.GetString(data, "stdout"));
    }

    [Fact]
    public void ReadsFromPocoProperties()
    {
        // list_directory returns FileSystemEntry objects, not dictionaries.
        var entry = new Entry { Name = "Program.cs", IsDirectory = false, LineCount = 42 };

        Assert.Equal("Program.cs", ToolData.GetString(entry, "name"));
        Assert.False(ToolData.GetBool(entry, "is_directory"));
        Assert.Equal(42, ToolData.GetInt(entry, "line_count"));
    }

    [Fact]
    public void KeyLookupIgnoresCaseAndSeparators()
    {
        var data = new Dictionary<string, object?> { ["LineCount"] = 7 };

        Assert.Equal(7, ToolData.GetInt(data, "line_count"));
        Assert.Equal(7, ToolData.GetInt(data, "lineCount"));
        Assert.Equal(7, ToolData.GetInt(data, "LINE-COUNT"));
    }

    [Fact]
    public void KeyLookupDoesNotMatchDifferentKeys()
    {
        var data = new Dictionary<string, object?> { ["line_count"] = 7 };

        Assert.Null(ToolData.GetInt(data, "lines"));
        Assert.Null(ToolData.GetInt(data, "line_counts"));
        Assert.Null(ToolData.GetInt(data, "count"));
    }

    [Fact]
    public void ReadsFromJsonElement()
    {
        using var doc = JsonDocument.Parse("""{"total_matches": 12, "pattern": "foo", "done": true}""");

        Assert.Equal(12, ToolData.GetInt(doc.RootElement, "total_matches"));
        Assert.Equal("foo", ToolData.GetString(doc.RootElement, "pattern"));
        Assert.True(ToolData.GetBool(doc.RootElement, "done"));
    }

    [Fact]
    public void TryGetAnyPrefersTheFirstPresentKey()
    {
        var data = new Dictionary<string, object?> { ["output"] = null, ["stdout"] = "from stdout" };

        // "output" exists but is null, so the search continues rather than stopping on it.
        Assert.Equal("from stdout", ToolData.GetString(data, "output", "stdout"));
    }

    [Fact]
    public void BlankStringsReadAsAbsent()
    {
        var data = new Dictionary<string, object?> { ["stderr"] = "   " };

        Assert.Null(ToolData.GetString(data, "stderr"));
    }

    [Fact]
    public void NumericsAreToleratedAcrossBoxedTypes()
    {
        Assert.Equal(5, ToolData.GetInt(new Dictionary<string, object?> { ["n"] = 5L }, "n"));
        Assert.Equal(5, ToolData.GetInt(new Dictionary<string, object?> { ["n"] = 5.0 }, "n"));
        Assert.Equal(5, ToolData.GetInt(new Dictionary<string, object?> { ["n"] = "5" }, "n"));
        Assert.Null(ToolData.GetInt(new Dictionary<string, object?> { ["n"] = "abc" }, "n"));
    }

    [Fact]
    public void DurationReadsMillisecondsAndTimeSpans()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1234),
            ToolData.GetDuration(new Dictionary<string, object?> { ["duration_ms"] = 1234.0 }, "duration_ms"));

        Assert.Equal(TimeSpan.FromSeconds(2),
            ToolData.GetDuration(new Dictionary<string, object?> { ["search_duration"] = TimeSpan.FromSeconds(2) }, "search_duration"));
    }

    [Fact]
    public void ListsExcludeStrings()
    {
        // A string is enumerable, but a caller asking for a list never wants its characters.
        Assert.Empty(ToolData.GetList(new Dictionary<string, object?> { ["items"] = "abc" }, "items"));

        var items = ToolData.GetList(new Dictionary<string, object?> { ["items"] = new[] { 1, 2, 3 } }, "items");
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void SplitLinesPreservesBlankLines()
    {
        // Blank lines carry meaning in diffs and formatted reports (#257); dropping them corrupts output.
        var lines = ToolData.SplitLines("a\r\n\r\nb\n");

        Assert.Equal(new[] { "a", "", "b", "" }, lines);
    }

    [Fact]
    public void MissingAndNullSourcesNeverThrow()
    {
        Assert.Null(ToolData.GetString(null, "anything"));
        Assert.Null(ToolData.GetInt("a bare string payload", "anything"));
        Assert.Empty(ToolData.GetList(null, "items"));
        Assert.False(ToolData.TryGet(null, "k", out _));
    }
}
