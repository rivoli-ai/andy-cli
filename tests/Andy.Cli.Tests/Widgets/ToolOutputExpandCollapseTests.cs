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
        public void RunningToolItem_Completed_DrawsGreenOrRedDotMarker()
        {
            var ok = new RunningToolItem("read_file_1", "read_file");
            ok.SetComplete(true, "0.1s");
            var okDot = RenderAndFindDot(ok);
            Assert.NotNull(okDot);
            Assert.Equal(new DL.Rgb24(0, 200, 0), okDot!.Value.Fg!.Value);

            var fail = new RunningToolItem("exec_1", "execute_command");
            fail.SetComplete(false, "0.1s");
            var failDot = RenderAndFindDot(fail);
            Assert.NotNull(failDot);
            Assert.Equal(new DL.Rgb24(200, 0, 0), failDot!.Value.Fg!.Value);
        }

        private static DL.TextRun? RenderAndFindDot(RunningToolItem item)
        {
            var b = new DL.DisplayListBuilder();
            item.RenderSlice(0, 0, Width, 0, item.MeasureLineCount(Width), new DL.DisplayListBuilder().Build(), b);
            foreach (var op in b.Build().Ops)
                if (op is DL.TextRun tr && tr.Content == "●")
                    return tr;
            return null;
        }

        [Fact]
        public void AddToolExecutionStart_AppendsTrailingSpacer()
        {
            var feed = new FeedView();
            feed.AddToolExecutionStart("t_1", "read_file");
            var items = feed.GetItemsForTesting();
            Assert.True(items.Count >= 2);
            Assert.IsType<RunningToolItem>(items[items.Count - 2]);
            Assert.IsType<SpacerItem>(items[items.Count - 1]);
        }

        [Fact]
        public void RunningToolItem_Command_ShowsActualOutput_NotLineCount()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("execute_command_1", "execute_command");
            item.SetComplete(true, "0.2s");
            item.SetResult("first output line\nsecond line\nthird line");

            var b = new DL.DisplayListBuilder();
            item.RenderSlice(0, 0, 80, 0, item.MeasureLineCount(80), new DL.DisplayListBuilder().Build(), b);
            var text = string.Concat(b.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content));

            Assert.Contains("first output line", text);   // shows the actual command output...
            Assert.DoesNotContain("lines output", text);   // ...not a "(N lines output)" count
        }

        [Fact]
        public void RunningToolItem_Command_ExpandedShowsMoreOutputThanCollapsed()
        {
            var output = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"output line {i}"));

            // ToolOutputView.Expanded is read at MeasureLineCount time, so measure each item while
            // the global flag matches the mode under test.
            ToolOutputView.Expanded = false;
            var collapsed = new RunningToolItem("execute_command_1", "execute_command");
            collapsed.SetComplete(true, "0.1s");
            collapsed.SetResult(output);
            int collapsedRows = collapsed.MeasureLineCount(80);

            ToolOutputView.Expanded = true;
            var expanded = new RunningToolItem("execute_command_2", "execute_command");
            expanded.SetComplete(true, "0.1s");
            expanded.SetResult(output);
            int expandedRows = expanded.MeasureLineCount(80);

            Assert.True(expandedRows > collapsedRows,
                $"expanded ({expandedRows}) command output should show more rows than collapsed ({collapsedRows})");
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

        [Fact]
        public void RunningToolItem_Command_Collapsed_ShowsUpToFiveOutputLines()
        {
            // #135: a multi-line command result should preview several lines collapsed (not just
            // one), capped at CollapsedResultPreviewLines (5), with a "+N more" hint for the rest.
            ToolOutputView.Expanded = false;
            var output = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"output line {i}"));
            var item = new RunningToolItem("execute_command_1", "execute_command");
            item.SetComplete(true, "0.1s");
            item.SetResult(output);

            var b = new DL.DisplayListBuilder();
            item.RenderSlice(0, 0, 80, 0, item.MeasureLineCount(80), new DL.DisplayListBuilder().Build(), b);
            var text = string.Concat(b.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content));

            Assert.Contains("output line 1", text);
            Assert.Contains("output line 5", text);     // multiple lines, not just the first
            Assert.DoesNotContain("output line 6", text); // capped at 5
            Assert.Contains("more lines", text);          // remainder hinted
        }

        [Fact]
        public void RunningToolItem_Command_Collapsed_MeasureMatchesRenderedRows_MultiLine()
        {
            // The multi-line collapsed preview must still satisfy the IFeedItem contract.
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("execute_command_1", "execute_command");
            item.SetComplete(true, "0.1s");
            item.SetResult(string.Join("\n", Enumerable.Range(1, 8).Select(i => $"line {i}")));

            Assert.Equal(item.MeasureLineCount(60), CountRenderedRows(item, 60));
        }
    }
}
