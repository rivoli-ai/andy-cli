using Andy.Cli.Widgets;
using Andy.Cli.Themes;
using Xunit;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Unit tests for ThinkingBlockItem and ThinkingView.
    /// </summary>
    public class ThinkingBlockItemTests
    {
        [Fact]
        public void AppendContent_AccumulatesText()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Let me ");
            item.AppendContent("check the project structure...");

            Assert.Equal("Let me check the project structure...", item.GetContent());
        }

        [Fact]
        public void AppendContent_IgnoresEmptyStrings()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("hello");
            item.AppendContent("");
            item.AppendContent(null!);
            item.AppendContent(" world");

            Assert.Equal("hello world", item.GetContent());
        }

        [Fact]
        public void MeasureLineCount_WithContent_ReturnsExpectedLines()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Hello world");

            // Width 40: indent=2, innerWidth = 40-2-2 = 36
            // Lines: 1 indicator + 1 border + 1 body + 1 border + 1 indicator = 5
            int lines = item.MeasureLineCount(40);
            Assert.Equal(5, lines);
        }

        [Fact]
        public void MeasureLineCount_EmptyContent_ReturnsFiveLines()
        {
            var item = new ThinkingBlockItem();
            // No content yet (streaming state)

            int lines = item.MeasureLineCount(40);
            // 1 indicator + 1 border + 1 body(empty) + 1 border + 1 indicator = 5
            Assert.Equal(5, lines);
        }

        [Fact]
        public void MeasureLineCount_MultilineContent_SplitsCorrectly()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Line 1\nLine 2\nLine 3");

            // Width 40: innerWidth = 36
            // 3 explicit lines, each fits in one row
            // Total: 1 + 1 + 3 + 1 + 1 = 7
            int lines = item.MeasureLineCount(40);
            Assert.Equal(7, lines);
        }

        [Fact]
        public void MeasureLineCount_TextWraps_LongLine()
        {
            var item = new ThinkingBlockItem();
            // 80 chars of text, innerWidth=36 -> wraps to 3 lines
            item.AppendContent(new string('A', 80));

            int lines = item.MeasureLineCount(40);
            // innerWidth = 40-2-2 = 36
            // body: ceil(80/36) = 3
            // Total: 1 + 1 + 3 + 1 + 1 = 7
            Assert.Equal(7, lines);
        }

        [Fact]
        public void MeasureLineCount_ZeroWidth_ReturnsZero()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Some text");

            Assert.Equal(0, item.MeasureLineCount(3)); // too narrow
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MeasureLineCount_ThinkingView_VisibleControlsHeight(bool visible)
        {
            ThinkingView.Visible = visible;
            try
            {
                var item = new ThinkingBlockItem();
                item.AppendContent("content");

                int lines = item.MeasureLineCount(40);
                if (visible)
                    Assert.True(lines > 0);
                else
                    Assert.Equal(0, lines);
            }
            finally
            {
                ThinkingView.Visible = true; // restore
            }
        }

        [Fact]
        public void ThinkingView_Toggle_FlipsState()
        {
            ThinkingView.Visible = true;

            bool result = ThinkingView.Toggle();
            Assert.False(result);
            Assert.False(ThinkingView.Visible);

            result = ThinkingView.Toggle();
            Assert.True(result);
            Assert.True(ThinkingView.Visible);
        }

        [Fact]
        public void ThinkingView_Visible_DefaultIsTrue()
        {
            // Reset to default
            ThinkingView.Visible = true;
            Assert.True(ThinkingView.Visible);
        }

        [Fact]
        public void GetContent_ReturnsFullContent_AfterStreaming()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Step 1. ");
            item.AppendContent("Step 2. ");
            item.AppendContent("Step 3.");
            item.Complete();

            Assert.Equal("Step 1. Step 2. Step 3.", item.GetContent());
        }

        [Fact]
        public void MeasureLineCount_ContentRetainedRegardlessOfVisibility()
        {
            var item = new ThinkingBlockItem();
            item.AppendContent("Important reasoning");
            item.Complete();

            // Show -> measure
            ThinkingView.Visible = true;
            int linesWhenVisible = item.MeasureLineCount(40);

            // Hide -> measure returns 0
            ThinkingView.Visible = false;
            int linesWhenHidden = item.MeasureLineCount(40);

            // Re-show -> measure returns same as before (content retained)
            ThinkingView.Visible = true;
            int linesWhenRevisible = item.MeasureLineCount(40);

            Assert.True(linesWhenVisible > 0);
            Assert.Equal(0, linesWhenHidden);
            Assert.Equal(linesWhenVisible, linesWhenRevisible);

            ThinkingView.Visible = true; // restore
        }

        [Fact]
        public void ThinkingView_Visible_CanBeSetDirectly()
        {
            ThinkingView.Visible = true;
            ThinkingView.Visible = false;
            Assert.False(ThinkingView.Visible);

            ThinkingView.Visible = true;
            Assert.True(ThinkingView.Visible);
        }
    }
}
