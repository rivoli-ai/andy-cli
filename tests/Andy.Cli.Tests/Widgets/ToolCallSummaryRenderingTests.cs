using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Rendering tests for the human-readable tool call summaries (#223): the collapsed
    /// header line shows a friendly action description ("Reading src/Program.cs") instead
    /// of the raw tool name + arguments, while the expanded view keeps the tool id and the
    /// full arguments. The "*" status marker and one-leading-space output contracts
    /// (#221/#225) must survive unchanged.
    /// </summary>
    public class ToolCallSummaryRenderingTests : IDisposable
    {
        private const int Width = 100;
        private readonly bool _originalExpanded;

        public ToolCallSummaryRenderingTests()
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

        private static string AllText(IEnumerable<DL.TextRun> runs)
            => string.Concat(runs.Select(r => r.Content));

        [Fact]
        public void ToolExecutionItem_Collapsed_ShowsHumanReadableSummary()
        {
            ToolOutputView.Expanded = false;
            var item = new ToolExecutionItem(
                "read_file",
                new Dictionary<string, object?> { ["file_path"] = "src/Program.cs" },
                "file contents", isSuccess: true);

            var text = AllText(RenderRuns(item));
            Assert.Contains("Reading src/Program.cs", text);
            // The raw name+args form is reserved for the expanded view.
            Assert.DoesNotContain("read_file", text);
            Assert.DoesNotContain("file_path=", text);
        }

        [Fact]
        public void ToolExecutionItem_Collapsed_KeepsStatusMarkerBeforeSummary()
        {
            ToolOutputView.Expanded = false;
            var item = new ToolExecutionItem(
                "read_file",
                new Dictionary<string, object?> { ["file_path"] = "src/Program.cs" },
                "ok", isSuccess: true);

            var runs = RenderRuns(item);
            var marker = runs.FirstOrDefault(r => r.Content == "*");
            Assert.Equal(new DL.Rgb24(0, 200, 0), marker.Fg!.Value);
            // The summary follows the marker on the same row.
            var header = runs.First(r => r.Content.Contains("Reading src/Program.cs"));
            Assert.Equal(marker.Y, header.Y);
        }

        [Fact]
        public void ToolExecutionItem_Expanded_StillShowsToolIdAndFullArguments()
        {
            ToolOutputView.Expanded = true;
            var item = new ToolExecutionItem(
                "read_file",
                new Dictionary<string, object?> { ["file_path"] = "src/Program.cs" },
                "file contents", isSuccess: true);

            var text = AllText(RenderRuns(item));
            Assert.Contains("read_file", text);
            Assert.Contains("Parameters:", text);
            Assert.Contains("file_path = src/Program.cs", text);
        }

        [Fact]
        public void RunningToolItem_Running_Collapsed_ShowsSummaryInsteadOfRawCall()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("execute_command_1", "execute_command");
            item.SetParameters(new Dictionary<string, object?> { ["command"] = "dotnet build" });

            var text = AllText(RenderRuns(item));
            Assert.Contains("Running: dotnet build", text);
            Assert.DoesNotContain("execute_command(", text);
        }

        [Fact]
        public void RunningToolItem_Completed_Collapsed_ShowsSummaryInsteadOfRawCall()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("read_file_1", "read_file");
            item.SetParameters(new Dictionary<string, object?> { ["file_path"] = "src/Program.cs" });
            item.SetComplete(true, "0.1s");

            var runs = RenderRuns(item);
            var text = AllText(runs);
            Assert.Contains("Reading src/Program.cs", text);
            Assert.DoesNotContain("read_file(", text);
            // The "*" completion marker survives next to the summary (#221).
            Assert.Contains(runs, r => r.Content == "*");
        }

        [Fact]
        public void RunningToolItem_Completed_Expanded_KeepsRawNameAndArguments()
        {
            ToolOutputView.Expanded = true;
            var item = new RunningToolItem("read_file_1", "read_file");
            item.SetParameters(new Dictionary<string, object?> { ["file_path"] = "src/Program.cs" });
            item.SetComplete(true, "0.1s");

            var text = AllText(RenderRuns(item));
            Assert.Contains("read_file(", text);
            Assert.Contains("src/Program.cs", text);
        }

        [Fact]
        public void RunningToolItem_Collapsed_TruncatesLongCommandInSummary()
        {
            ToolOutputView.Expanded = false;
            var longCommand = "dotnet test " + new string('x', 200);
            var item = new RunningToolItem("execute_command_1", "execute_command");
            item.SetParameters(new Dictionary<string, object?> { ["command"] = longCommand });
            item.SetComplete(true, "0.1s");

            var text = AllText(RenderRuns(item));
            Assert.Contains("Running: dotnet test", text);
            Assert.DoesNotContain(longCommand, text);
        }

        [Fact]
        public void RunningToolItem_Collapsed_UnknownTool_ShowsCleanedUpNameNotJson()
        {
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("mystery_tool_1", "mystery_tool");
            item.SetParameters(new Dictionary<string, object?> { ["query"] = "answers" });
            item.SetComplete(true, "0.1s");

            var text = AllText(RenderRuns(item));
            Assert.Contains("Mystery tool: answers", text);
            Assert.DoesNotContain("{", text);
        }

        [Fact]
        public void RunningToolItem_Collapsed_OutputLinesKeepSingleLeadingSpace()
        {
            // The summary header must not disturb the one-leading-space output contract (#225).
            ToolOutputView.Expanded = false;
            var item = new RunningToolItem("execute_command_1", "execute_command");
            item.SetParameters(new Dictionary<string, object?> { ["command"] = "ls" });
            item.SetComplete(true, "0.1s");
            item.SetResult("first output line\nsecond output line");

            var runs = RenderRuns(item);
            var first = runs.First(r => r.Content.Contains("first output line"));
            Assert.StartsWith(" L first output line", first.Content);
        }
    }
}
