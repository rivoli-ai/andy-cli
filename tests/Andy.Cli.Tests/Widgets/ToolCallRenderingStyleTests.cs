using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Rendering-style regression tests for tool call feed items:
    ///  - #221: the colored status marker next to tool calls is a small ASCII
    ///    asterisk ("*"), not a large filled-dot glyph, so tool lines stay compact;
    ///  - #225: tool output lines carry exactly one leading space (the "L" gutter
    ///    is " L " and plain output lines are indented by a single space), instead
    ///    of the previous 2-4 leading blanks.
    /// </summary>
    public class ToolCallRenderingStyleTests : IDisposable
    {
        private const int Width = 80;
        private readonly bool _originalExpanded;

        public ToolCallRenderingStyleTests()
        {
            _originalExpanded = ToolOutputView.Expanded;
        }

        public void Dispose()
        {
            ToolOutputView.Expanded = _originalExpanded;
        }

        private static List<DL.TextRun> RenderRuns(IFeedItem item, int width = Width)
        {
            var b = new DL.DisplayListBuilder();
            var baseDl = new DL.DisplayListBuilder().Build();
            item.RenderSlice(0, 0, width, 0, item.MeasureLineCount(width), baseDl, b);
            return b.Build().Ops.OfType<DL.TextRun>().ToList();
        }

        private static DL.TextRun? Find(IEnumerable<DL.TextRun> runs, Func<DL.TextRun, bool> predicate)
        {
            foreach (var r in runs)
                if (predicate(r)) return r;
            return null;
        }

        [Fact]
        public void RunningToolItem_Completed_MarkerIsSmallAsciiAsterisk()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("read_file_1", "read_file");
            item.SetComplete(true, "0.1s");
            item.SetResult("some result");

            var runs = RenderRuns(item);
            Assert.NotNull(Find(runs, r => r.Content == "*"));
            // No large non-ASCII bullet anywhere in the rendered output.
            Assert.Null(Find(runs, r => r.Content.Contains('●')));
        }

        [Fact]
        public void ToolExecutionItem_Header_MarkerIsSmallAsciiAsterisk()
        {
            ToolOutputView.Expanded = false;
            var item = new ToolExecutionItem("read_file", new Dictionary<string, object?>(), "ok", isSuccess: true);

            var runs = RenderRuns(item);
            var marker = Find(runs, r => r.Content == "*");
            Assert.NotNull(marker);
            Assert.Equal(new DL.Rgb24(0, 200, 0), marker!.Value.Fg!.Value);
            Assert.Null(Find(runs, r => r.Content.Contains('●')));
        }

        [Fact]
        public void ToolExecutionItem_Header_MarkerIsRedOnFailure()
        {
            ToolOutputView.Expanded = false;
            var item = new ToolExecutionItem("read_file", new Dictionary<string, object?>(), "boom", isSuccess: false);

            var runs = RenderRuns(item);
            var marker = Find(runs, r => r.Content == "*");
            Assert.NotNull(marker);
            Assert.Equal(new DL.Rgb24(200, 0, 0), marker!.Value.Fg!.Value);
        }

        [Fact]
        public void RunningToolItem_MultiLineOutput_FirstLineHasSingleLeadingSpace()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("execute_command_1", "execute_command");
            item.SetComplete(true, "0.1s");
            item.SetResult("first output line\nsecond output line");

            var runs = RenderRuns(item);
            var first = Find(runs, r => r.Content.Contains("first output line"));
            Assert.NotNull(first);
            // Exactly one leading space before the "L" gutter marker.
            Assert.StartsWith(" L first output line", first!.Value.Content);
        }

        [Fact]
        public void RunningToolItem_MultiLineOutput_ContinuationAlignsUnderFirstLine()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("execute_command_1", "execute_command");
            item.SetComplete(true, "0.1s");
            item.SetResult("first output line\nsecond output line");

            var runs = RenderRuns(item);
            var second = Find(runs, r => r.Content.Contains("second output line"));
            Assert.NotNull(second);
            // Continuation lines align under the first line's content (" L " is 3 cells wide).
            Assert.StartsWith("   second output line", second!.Value.Content);
            Assert.False(second.Value.Content.StartsWith("    ", StringComparison.Ordinal),
                "continuation output lines must not carry extra leading whitespace");
        }

        [Fact]
        public void RunningToolItem_SummaryResult_HasSingleLeadingSpace()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("list_1", "list_directory");
            item.SetComplete(true, "0.1s");
            // No result set -> uniform "done" summary line.

            var runs = RenderRuns(item);
            var result = Find(runs, r => r.Content.Contains("done"));
            Assert.NotNull(result);
            Assert.StartsWith(" L done", result!.Value.Content);
        }

        [Fact]
        public void RunningToolItem_RunningStatus_HasSingleLeadingSpace()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("read_file_1", "read_file");
            // Not complete: renders the live "Running..." status row.

            var runs = RenderRuns(item);
            var status = Find(runs, r => r.Content.Contains("Running..."));
            Assert.NotNull(status);
            Assert.StartsWith(" L Running...", status!.Value.Content);
        }

        [Fact]
        public void ToolExecutionItem_ExpandedResultLines_HaveSingleLeadingSpace()
        {
            ToolOutputView.Expanded = true;
            var item = new ToolExecutionItem(
                "read_file",
                new Dictionary<string, object?>(),
                "alpha line\nbeta line",
                isSuccess: true);

            var runs = RenderRuns(item);
            var alpha = Find(runs, r => r.Content.Contains("alpha line"));
            var beta = Find(runs, r => r.Content.Contains("beta line"));
            Assert.NotNull(alpha);
            Assert.NotNull(beta);
            Assert.Equal(" alpha line", alpha!.Value.Content);
            Assert.Equal(" beta line", beta!.Value.Content);
        }
    }
}
