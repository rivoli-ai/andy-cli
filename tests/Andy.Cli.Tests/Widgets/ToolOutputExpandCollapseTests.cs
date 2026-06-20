using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Verifies the Ctrl+O expand/collapse VIEW toggle on tool feed items:
    ///  - MeasureLineCount equals the number of distinct rows RenderSlice draws
    ///    (the IFeedItem contract — a mismatch produces phantom blank lines), in
    ///    both collapsed and expanded modes;
    ///  - expanded mode shows strictly more content than collapsed mode.
    /// </summary>
    public class ToolOutputExpandCollapseTests : IDisposable
    {
        private readonly bool _originalExpanded;

        public ToolOutputExpandCollapseTests()
        {
            _originalExpanded = ToolOutputView.Expanded;
        }

        public void Dispose()
        {
            // The flag is process-wide; restore it so tests don't bleed into each other.
            ToolOutputView.Expanded = _originalExpanded;
        }

        private const int Width = 60;

        // Count the distinct rows actually drawn by rendering into a probe display list
        // and tallying unique Y coordinates of non-empty text runs.
        private static int CountRenderedRows(IFeedItem item, int width)
        {
            int measured = item.MeasureLineCount(width);
            var probe = new DL.DisplayListBuilder();
            var probeBase = new DL.DisplayListBuilder().Build();
            // Render the full item (startLine 0, maxLines == measured) so every reserved
            // row gets a chance to draw.
            item.RenderSlice(0, 0, width, 0, measured, probeBase, probe);

            var rows = new HashSet<int>();
            foreach (var op in probe.Build().Ops)
            {
                if (op is DL.TextRun tr && !string.IsNullOrEmpty(tr.Content))
                    rows.Add(tr.Y);
            }
            return rows.Count;
        }

        private static Dictionary<string, object?> SampleParams() => new()
        {
            ["file_path"] = "/some/very/long/path/to/a/source/file/that/wraps.cs",
            ["limit"] = 200,
            ["offset"] = 10,
            ["pattern"] = "TODO|FIXME|HACK",
        };

        private const string SampleResult =
            "line one of the result\n" +
            "line two is a fairly long line that should wrap when rendered into a narrow column\n" +
            "line three\nline four\nline five";

        [Fact]
        public void ToolExecutionItem_MeasureMatchesRenderedRows_Collapsed()
        {
            ToolOutputView.Expanded = false;
            var item = new ToolExecutionItem("read_file", SampleParams(), SampleResult, isSuccess: true);

            int measured = item.MeasureLineCount(Width);
            int rendered = CountRenderedRows(item, Width);

            Assert.Equal(measured, rendered);
        }

        [Fact]
        public void ToolExecutionItem_MeasureMatchesRenderedRows_Expanded()
        {
            ToolOutputView.Expanded = true;
            var item = new ToolExecutionItem("read_file", SampleParams(), SampleResult, isSuccess: true);

            int measured = item.MeasureLineCount(Width);
            int rendered = CountRenderedRows(item, Width);

            Assert.Equal(measured, rendered);
        }

        [Fact]
        public void ToolExecutionItem_ExpandedShowsMoreThanCollapsed()
        {
            var paramsDict = SampleParams();

            ToolOutputView.Expanded = false;
            int collapsed = new ToolExecutionItem("read_file", paramsDict, SampleResult, true).MeasureLineCount(Width);

            ToolOutputView.Expanded = true;
            int expanded = new ToolExecutionItem("read_file", paramsDict, SampleResult, true).MeasureLineCount(Width);

            Assert.True(expanded > collapsed,
                $"expected expanded ({expanded}) > collapsed ({collapsed})");
        }

        [Fact]
        public void RunningToolItem_Completed_MeasureMatchesRenderedRows_Collapsed()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("read_file_1", "read_file");
            item.SetParameters(SampleParams());
            item.SetComplete(true, "1.2s");
            item.SetResult(SampleResult);

            int measured = item.MeasureLineCount(Width);
            int rendered = CountRenderedRows(item, Width);

            Assert.Equal(measured, rendered);
        }

        [Fact]
        public void RunningToolItem_Completed_MeasureMatchesRenderedRows_Expanded()
        {
            ToolOutputView.Expanded = true;
            var item = new RunningToolItem("read_file_1", "read_file");
            item.SetParameters(SampleParams());
            item.SetComplete(true, "1.2s");
            item.SetResult(SampleResult);

            int measured = item.MeasureLineCount(Width);
            int rendered = CountRenderedRows(item, Width);

            Assert.Equal(measured, rendered);
        }

        [Fact]
        public void RunningToolItem_Completed_ExpandedShowsMoreThanCollapsed()
        {
            ToolOutputView.Expanded = false;
            var collapsedItem = new RunningToolItem("read_file_1", "read_file");
            collapsedItem.SetParameters(SampleParams());
            collapsedItem.SetComplete(true, "1.2s");
            collapsedItem.SetResult(SampleResult);
            int collapsed = collapsedItem.MeasureLineCount(Width);

            ToolOutputView.Expanded = true;
            var expandedItem = new RunningToolItem("read_file_2", "read_file");
            expandedItem.SetParameters(SampleParams());
            expandedItem.SetComplete(true, "1.2s");
            expandedItem.SetResult(SampleResult);
            int expanded = expandedItem.MeasureLineCount(Width);

            Assert.True(expanded > collapsed,
                $"expected expanded ({expanded}) > collapsed ({collapsed})");
        }

        [Fact]
        public void RunningToolItem_CompletedWithNoResult_StillRendersUniformResultLine()
        {
            // Consistency: a completed tool with no summary (e.g. an empty directory listing) must
            // still render a result line ("done"), not skip it — so every tool has the same shape.
            ToolOutputView.Expanded = false;
            var noResult = new RunningToolItem("list_1", "list_directory");
            noResult.SetComplete(true, "0.5s");
            // no SetResult call -> empty summary

            int measured = noResult.MeasureLineCount(Width);
            Assert.Equal(measured, CountRenderedRows(noResult, Width));
            Assert.True(measured >= 2, "completed tool should reserve a header + a result row");

            var probe = new DL.DisplayListBuilder();
            noResult.RenderSlice(0, 0, Width, 0, measured, new DL.DisplayListBuilder().Build(), probe);
            var text = string.Concat(probe.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content));
            Assert.Contains("done", text);
        }

        [Fact]
        public void Toggle_FlipsState()
        {
            ToolOutputView.Expanded = false;
            Assert.True(ToolOutputView.Toggle());
            Assert.True(ToolOutputView.Expanded);
            Assert.False(ToolOutputView.Toggle());
            Assert.False(ToolOutputView.Expanded);
        }
    }
}
