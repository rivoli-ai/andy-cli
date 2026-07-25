using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Widgets;
using Andy.Cli.Widgets.Tools;
using Xunit;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// The shared tool feed item (#249). The load-bearing property is that MeasureLineCount and
/// RenderSlice agree exactly: a mismatch leaves phantom blank rows in the feed.
/// </summary>
public class ToolCallItemTests
{
    private sealed class FixedPresenter : IToolPresenter
    {
        private readonly ToolPresentation _presentation;
        public FixedPresenter(ToolPresentation presentation) => _presentation = presentation;
        public bool CanPresent(string toolName) => true;
        public ToolPresentation Present(ToolCallSnapshot s, ToolPresentationContext c) => _presentation;
    }

    private static ToolCallSnapshot Snapshot(bool complete = true, bool success = true) => new()
    {
        ToolId = "demo_1",
        ToolName = "demo",
        IsComplete = complete,
        IsSuccessful = success
    };

    /// <summary>
    /// Counts the rows a render actually draws, by recording the y coordinates the item touched.
    /// </summary>
    private static int RenderedRowCount(IFeedItem item, int width, int maxLines = 200)
    {
        var builder = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, maxLines, new DL.DisplayListBuilder().Build(), builder);

        var rows = builder.Build().Ops.OfType<DL.TextRun>()
            .Where(t => !string.IsNullOrEmpty(t.Content))
            .Select(t => t.Y)
            .Distinct()
            .ToList();
        return rows.Count == 0 ? 0 : rows.Max() + 1;
    }

    [Fact]
    public void MeasuredRowsEqualRenderedRows()
    {
        var presentation = new ToolPresentation
        {
            Header = StyledLine.Plain("Ran a fairly long command that will need to wrap somewhere"),
            Trailing = "1.2s",
            Body = new[]
            {
                StyledLine.Plain("first output line"),
                StyledLine.Plain("second output line"),
                StyledLine.Plain("third output line")
            }
        };
        var item = new ToolCallItem(Snapshot(), new FixedPresenter(presentation));

        foreach (var width in new[] { 20, 40, 61, 80, 120 })
        {
            Assert.Equal(item.MeasureLineCount(width), RenderedRowCount(item, width));
        }
    }

    [Fact]
    public void HeaderWrapsInsteadOfBeingTruncated()
    {
        var text = "Ran dotnet build --configuration Release --no-restore /p:ContinuousIntegrationBuild=true";
        var item = new ToolCallItem(Snapshot(),
            new FixedPresenter(ToolPresentation.Line(StyledLine.Plain(text))));

        var rows = item.DebugRows(40);

        Assert.True(rows.Count > 1, "a long header must wrap onto more rows");
        // Every character of the command survives somewhere in the rows.
        var joined = string.Concat(rows.Select(r => r.Replace("* ", "").Trim()));
        Assert.Contains("ContinuousIntegrationBuild=true", joined);
        Assert.DoesNotContain("...", joined);
    }

    [Fact]
    public void TrailingMetricRidesOnTheHeaderWhenItFits()
    {
        var item = new ToolCallItem(Snapshot(),
            new FixedPresenter(ToolPresentation.Line(StyledLine.Plain("Ran ls"), "1.2s")));

        Assert.Contains("1.2s", item.DebugRows(60)[0]);
    }

    [Fact]
    public void TrailingMetricIsDroppedRatherThanWrappedOnNarrowTerminals()
    {
        // A duration alone on a continuation row reads as content, which it is not.
        var item = new ToolCallItem(Snapshot(),
            new FixedPresenter(ToolPresentation.Line(StyledLine.Plain("Ran a command with a long name"), "1.2s")));

        var rows = item.DebugRows(24);

        Assert.DoesNotContain(rows, r => r.Trim() == "1.2s");
    }

    [Fact]
    public void InlineBodyRowsGetTheGutter()
    {
        var item = new ToolCallItem(Snapshot(), new FixedPresenter(new ToolPresentation
        {
            Header = StyledLine.Plain("Read src/Foo.cs"),
            Body = new[] { StyledLine.Plain("240 lines"), StyledLine.Plain("utf-8") },
            Layout = ToolLayout.Inline
        }));

        var rows = item.DebugRows(60);

        Assert.Equal("  L 240 lines", rows[1]);
        Assert.Equal("    utf-8", rows[2]);
    }

    [Fact]
    public void StatusGlyphReflectsTheTerminalState()
    {
        StyledLine Header() => StyledLine.Plain("did a thing");
        var presenter = new FixedPresenter(ToolPresentation.Line(Header()));

        Assert.StartsWith("*", new ToolCallItem(Snapshot(complete: true, success: true), presenter).DebugRows(40)[0]);
        Assert.StartsWith("x", new ToolCallItem(Snapshot(complete: true, success: false), presenter).DebugRows(40)[0]);

        var denied = new ToolCallItem(Snapshot() with { WasDenied = true }, presenter);
        Assert.StartsWith("-", denied.DebugRows(40)[0]);

        var cancelled = new ToolCallItem(Snapshot() with { WasCancelled = true }, presenter);
        Assert.StartsWith("-", cancelled.DebugRows(40)[0]);
    }

    [Fact]
    public void UpdatingTheSnapshotRebuildsThePlan()
    {
        var presenter = new CountingPresenter();
        var item = new ToolCallItem(Snapshot(complete: false), presenter);

        item.MeasureLineCount(60);
        item.MeasureLineCount(60);
        Assert.Equal(1, presenter.Calls);   // cached between frames at the same width

        item.Update(s => s with { IsComplete = true, IsSuccessful = true });
        item.MeasureLineCount(60);
        Assert.Equal(2, presenter.Calls);   // re-presented after the state changed
    }

    private sealed class CountingPresenter : IToolPresenter
    {
        public int Calls { get; private set; }
        public bool CanPresent(string toolName) => true;
        public ToolPresentation Present(ToolCallSnapshot s, ToolPresentationContext c)
        {
            Calls++;
            return ToolPresentation.Line(StyledLine.Plain("x"));
        }
    }

    [Fact]
    public void NeverMeasuresZeroRows()
    {
        var item = new ToolCallItem(Snapshot(),
            new FixedPresenter(ToolPresentation.Line(StyledLine.Empty)));

        Assert.True(item.MeasureLineCount(40) >= 1);
    }
}
