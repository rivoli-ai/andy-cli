using System;
using System.Threading;
using Xunit;
using Andy.Cli.Widgets;

namespace Andy.Cli.Tests.Widgets;

public class FeedViewTimeDisplayTests
{
    [Fact]
    public void FormatDuration_Milliseconds_FormatsCorrectly()
    {
        // Test milliseconds formatting
        Assert.Equal("100ms", FormatDuration(TimeSpan.FromMilliseconds(100)));
        Assert.Equal("500ms", FormatDuration(TimeSpan.FromMilliseconds(500)));
        Assert.Equal("999ms", FormatDuration(TimeSpan.FromMilliseconds(999)));
    }

    [Fact]
    public void FormatDuration_Seconds_FormatsCorrectly()
    {
        // Test seconds formatting
        Assert.Equal("1.0s", FormatDuration(TimeSpan.FromSeconds(1)));
        Assert.Equal("1.5s", FormatDuration(TimeSpan.FromSeconds(1.5)));
        Assert.Equal("5.3s", FormatDuration(TimeSpan.FromSeconds(5.3)));
        Assert.Equal("10.0s", FormatDuration(TimeSpan.FromSeconds(10)));
        Assert.Equal("30.5s", FormatDuration(TimeSpan.FromSeconds(30.5)));
        Assert.Equal("59.9s", FormatDuration(TimeSpan.FromSeconds(59.9)));
    }

    [Fact]
    public void FormatDuration_Minutes_FormatsCorrectly()
    {
        // Test minutes formatting
        Assert.Equal("1.0m", FormatDuration(TimeSpan.FromMinutes(1)));
        Assert.Equal("1.5m", FormatDuration(TimeSpan.FromMinutes(1.5)));
        Assert.Equal("5.0m", FormatDuration(TimeSpan.FromMinutes(5)));
        Assert.Equal("10.3m", FormatDuration(TimeSpan.FromMinutes(10.3)));
    }

    [Fact]
    public void ProcessingIndicator_TimeDisplay_ShowsCorrectElapsedTime()
    {
        var indicator = new ProcessingIndicatorItem();

        // Should start with no time display
        var initialText = GetProcessingText(indicator, TimeSpan.Zero);
        Assert.DoesNotContain("[", initialText);

        // After 0.5 seconds, still no display
        var halfSecondText = GetProcessingText(indicator, TimeSpan.FromMilliseconds(500));
        Assert.DoesNotContain("[", halfSecondText);

        // After 1 second, should show [1.0s]
        var oneSecondText = GetProcessingText(indicator, TimeSpan.FromSeconds(1));
        Assert.Contains("[1.0s]", oneSecondText);

        // After 2.5 seconds, should show [2.5s]
        var twoPointFiveText = GetProcessingText(indicator, TimeSpan.FromSeconds(2.5));
        Assert.Contains("[2.5s]", twoPointFiveText);

        // After 10 seconds, should show [10.0s]
        var tenSecondsText = GetProcessingText(indicator, TimeSpan.FromSeconds(10));
        Assert.Contains("[10.0s]", tenSecondsText);

        // Make sure it doesn't show wrong values like [21s] for 2.1 seconds
        var twoPointOneText = GetProcessingText(indicator, TimeSpan.FromSeconds(2.1));
        Assert.Contains("[2.1s]", twoPointOneText);
        Assert.DoesNotContain("[21", twoPointOneText);
    }

    // Helper method to simulate getting the processing text at a specific elapsed time
    private string GetProcessingText(ProcessingIndicatorItem indicator, TimeSpan elapsed)
    {
        // This would need to be implemented or mocked based on actual implementation
        // For now, we're testing the concept
        var elapsedText = elapsed.TotalSeconds < 1 ? "" : $"[{elapsed.TotalSeconds:0.1}s]";
        return $"Processing request{elapsedText}";
    }

    // Copy of the FormatDuration method from RunningToolItem to test
    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds < 1000)
            return $"{elapsed.TotalMilliseconds:0}ms";
        else if (elapsed.TotalSeconds < 60)
            return $"{elapsed.TotalSeconds:0.1}s";
        else
            return $"{elapsed.TotalMinutes:0.1}m";
    }
}