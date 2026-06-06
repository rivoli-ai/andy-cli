using System.Collections.Generic;
using System.Reflection;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Regression tests for command-output width: when a command (execute_command)
    /// runs, its single-line result/summary shown in the feed must use the full
    /// available feed width instead of being clipped to a narrow hard-coded cap
    /// (~80 chars, which is roughly 1/3 of a wide terminal).
    /// </summary>
    public class CommandOutputWidthTests : ToolRenderingTestBase
    {
        private static void SetAvailableWidth(RunningToolItem item, int width)
        {
            var field = typeof(RunningToolItem).GetField(
                "_availableWidth",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field!.SetValue(item, width);
        }

        private static string Render(RunningToolItem item, int width)
        {
            // Render the item into a fresh DisplayList at the given width. This sets
            // the internal available-width used by the summary helpers, mirroring how
            // FeedView renders feed items.
            var baseDl = new DL.DisplayListBuilder().Build();
            var b = new DL.DisplayListBuilder();
            item.RenderSlice(0, 0, width, 0, 10, baseDl, b);
            b.Build();
            return GetResultSummary(item);
        }

        [Fact]
        public void CommandError_Summary_UsesFullWidth_NotNarrowCap()
        {
            // A long single-line command error. Old behavior capped the summary at 80
            // chars regardless of terminal width.
            var longOutput = new string('x', 400);
            var parameters = new Dictionary<string, object?> { { "command", "ls -la" } };

            var item = CreateToolItem("execute_command", parameters);
            item.SetComplete(false, "0.5s");
            item.SetResult(longOutput);

            // Render at a wide width (240 columns).
            var wide = Render(item, 240);
            // Render at a narrow width (80 columns).
            var narrowItem = CreateToolItem("execute_command", parameters);
            narrowItem.SetComplete(false, "0.5s");
            narrowItem.SetResult(longOutput);
            var narrow = Render(narrowItem, 80);

            // The wide render must produce a noticeably longer summary than the narrow
            // one: the content now scales with available width instead of a 80-char cap.
            Assert.True(wide.Length > narrow.Length,
                $"Expected wide summary ({wide.Length}) to be longer than narrow ({narrow.Length})");

            // And the wide summary should clearly exceed the old ~80-char limit.
            Assert.True(wide.Length > 120,
                $"Expected wide command summary to exceed the old narrow cap, got {wide.Length} chars");
        }

        [Fact]
        public void CommandError_Summary_DefaultWidth_RemainsBounded()
        {
            // Without rendering, the default available width (80) keeps summaries bounded
            // so non-rendering callers (e.g. plain summary inspection) stay reasonable.
            var longOutput = new string('y', 400);
            var parameters = new Dictionary<string, object?> { { "command", "echo hi" } };

            var item = CreateToolItem("execute_command", parameters);
            item.SetComplete(false, "0.5s");
            item.SetResult(longOutput);

            var summary = GetResultSummary(item);
            Assert.True(summary.Length <= 80,
                $"Expected default summary to stay within the 80-char default, got {summary.Length}");
        }

        [Fact]
        public void CommandTool_RenderSlice_WideWidth_DoesNotThrow()
        {
            var parameters = new Dictionary<string, object?> { { "command", "find / -name '*.cs'" } };
            var item = CreateToolItem("execute_command", parameters);
            item.SetComplete(true, "1.0s");
            item.SetResult(new string('z', 500));

            var baseDl = new DL.DisplayListBuilder().Build();
            var b = new DL.DisplayListBuilder();

            // Should render cleanly across a range of widths, including very wide ones.
            item.RenderSlice(0, 0, 240, 0, 10, baseDl, b);
            item.RenderSlice(0, 0, 40, 0, 10, baseDl, b);
            b.Build();
        }
    }
}
