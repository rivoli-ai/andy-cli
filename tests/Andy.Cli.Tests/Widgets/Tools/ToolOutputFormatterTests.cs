using System;
using System.Linq;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Output-budget tests (#250): wrap before truncating, keep the head AND the tail, never
/// hard-truncate a row.
/// </summary>
public class ToolOutputFormatterTests
{
    private static string Lines(int count) =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => $"line {i}"));

    [Fact]
    public void ShortOutputIsReturnedWhole()
    {
        var result = ToolOutputFormatter.Format(Lines(3), width: 40, maxRows: 6);

        Assert.Equal(3, result.Rows.Count);
        Assert.False(result.WasTruncated);
        Assert.Equal(new[] { "line 1", "line 2", "line 3" }, result.Rows.Select(r => r.Text));
    }

    [Fact]
    public void LongOutputKeepsTheHeadAndTheTail()
    {
        // The tail is where a failing command's error lives; head-only truncation dropped it.
        var result = ToolOutputFormatter.Format(Lines(100), width: 40, maxRows: 5);

        Assert.Equal(5, result.Rows.Count);
        Assert.Equal("line 1", result.Rows[0].Text);
        Assert.Equal("line 2", result.Rows[1].Text);
        Assert.Contains("+96 lines", result.Rows[2].Text);
        Assert.Equal("line 99", result.Rows[3].Text);
        Assert.Equal("line 100", result.Rows[4].Text);
        Assert.Equal(96, result.OmittedRows);
        Assert.Equal(100, result.TotalRows);
    }

    [Fact]
    public void BudgetIsMeasuredInScreenRowsNotLogicalLines()
    {
        // Three logical lines that each wrap to ~10 rows would previously pass a 5-line cap and
        // then occupy 30 rows on screen.
        var wide = string.Join("\n", Enumerable.Repeat(new string('x', 200), 3));

        var result = ToolOutputFormatter.Format(wide, width: 20, maxRows: 5);

        Assert.Equal(5, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.True(r.Width <= 20 + 20, "no row may exceed the width budget"));
        Assert.Equal(30, result.TotalRows);
    }

    [Fact]
    public void RowsAreWrappedNotTruncated()
    {
        var result = ToolOutputFormatter.Format("the quick brown fox jumps over the lazy dog",
            width: 12, maxRows: 20);

        // Every character survives; nothing is replaced by an ellipsis.
        var joined = string.Concat(result.Rows.Select(r => r.Text));
        Assert.DoesNotContain("...", joined);
        Assert.Contains("dog", joined);
        Assert.All(result.Rows, r => Assert.True(r.Width <= 12));
    }

    [Fact]
    public void OverlongTokensAreHardBrokenRatherThanLost()
    {
        var token = new string('a', 50);

        var result = ToolOutputFormatter.Format(token, width: 10, maxRows: 20);

        Assert.Equal(50, result.Rows.Sum(r => r.Width));
        Assert.All(result.Rows, r => Assert.True(r.Width <= 10));
    }

    [Fact]
    public void AnsiColorsSurviveIntoTheRows()
    {
        var result = ToolOutputFormatter.Format("\u001b[31mfailed\u001b[0m", width: 40, maxRows: 5);

        Assert.Single(result.Rows);
        Assert.Equal("failed", result.Rows[0].Text);
        Assert.Equal(Andy.Cli.Themes.Theme.Current.Error, result.Rows[0].Spans[0].Foreground);
    }

    [Fact]
    public void TrailingBlankRowsAreDropped()
    {
        // A final newline is punctuation, not a blank row of content.
        var result = ToolOutputFormatter.Format("only line\n\n\n", width: 40, maxRows: 5);

        Assert.Single(result.Rows);
    }

    [Fact]
    public void InteriorBlankRowsArePreserved()
    {
        // Blank lines carry meaning in diffs and formatted reports (#257).
        var result = ToolOutputFormatter.Format("a\n\nb", width: 40, maxRows: 10);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.Rows[1].IsEmpty);
    }

    [Fact]
    public void EmptyOutputProducesNothing()
    {
        Assert.Empty(ToolOutputFormatter.Format("", width: 40, maxRows: 5).Rows);
        Assert.Empty(ToolOutputFormatter.Format(null, width: 40, maxRows: 5).Rows);
    }

    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(450, "450ms")]
    [InlineData(1500, "1.5s")]
    [InlineData(65000, "1m05s")]
    public void DurationsFormatCompactly(int milliseconds, string expected)
    {
        Assert.Equal(expected, ToolOutputFormatter.FormatDuration(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void CountsAreThousandsSeparated()
    {
        Assert.Equal("12,481", ToolOutputFormatter.FormatCount(12481));
        Assert.Equal("1 match", ToolOutputFormatter.Pluralize(1, "match", "matches"));
        Assert.Equal("2 matches", ToolOutputFormatter.Pluralize(2, "match", "matches"));
        Assert.Equal("3 files", ToolOutputFormatter.Pluralize(3, "file"));
    }
}
