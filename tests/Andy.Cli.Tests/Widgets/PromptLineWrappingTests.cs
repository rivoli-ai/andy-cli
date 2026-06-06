using System;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Tests for soft-wrapping of the prompt input and cursor navigation across
    /// wrapped (visual) rows. The prompt must never overflow horizontally; long
    /// logical lines wrap to additional visual rows at the configured wrap width.
    /// </summary>
    public class PromptLineWrappingTests
    {
        private static ConsoleKeyInfo Char(char c)
            => new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false);

        private static ConsoleKeyInfo Key(ConsoleKey key, bool ctrl = false)
            => new ConsoleKeyInfo('\0', key, false, false, ctrl);

        private static PromptLine MakePrompt(int wrapWidth, string text = "")
        {
            var p = new PromptLine();
            p.SetWrapWidth(wrapWidth);
            if (text.Length > 0) p.SetText(text);
            return p;
        }

        [Fact]
        public void ShortText_FitsInSingleVisualRow()
        {
            var p = MakePrompt(10, "hello");
            Assert.Equal(1, p.GetLineCount());
        }

        [Fact]
        public void LongText_WrapsToMultipleVisualRows()
        {
            // 25 chars at width 10 => 3 visual rows (10 + 10 + 5).
            var p = MakePrompt(10, new string('a', 25));
            Assert.Equal(3, p.GetLineCount());
        }

        [Fact]
        public void TextExactlyAtWidth_StaysSingleRow()
        {
            var p = MakePrompt(10, new string('a', 10));
            Assert.Equal(1, p.GetLineCount());
        }

        [Fact]
        public void TextOneOverWidth_WrapsToTwoRows()
        {
            var p = MakePrompt(10, new string('a', 11));
            Assert.Equal(2, p.GetLineCount());
        }

        [Fact]
        public void ExplicitNewlinesAndWrapping_CombineForVisualRowCount()
        {
            // "aaaaaaaaaaaa\nbb" at width 10 => row "aaaaaaaaaa", "aa", "bb" => 3 rows.
            var p = MakePrompt(10, new string('a', 12) + "\n" + "bb");
            Assert.Equal(3, p.GetLineCount());
        }

        [Fact]
        public void WrappingIncreasesDesiredHeight()
        {
            var single = MakePrompt(10, "abc");
            var wrapped = MakePrompt(10, new string('a', 35)); // 4 visual rows
            Assert.True(wrapped.GetDesiredHeight() > single.GetDesiredHeight());
            // 4 content rows + 2 borders.
            Assert.Equal(6, wrapped.GetDesiredHeight());
        }

        [Fact]
        public void ZeroWrapWidth_DisablesWrapping()
        {
            var p = MakePrompt(0, new string('a', 100));
            Assert.Equal(1, p.GetLineCount());
        }

        [Fact]
        public void EmptyText_IsSingleRow()
        {
            var p = MakePrompt(10, "");
            Assert.Equal(1, p.GetLineCount());
        }

        [Fact]
        public void TrailingNewline_ProducesExtraRow()
        {
            var p = MakePrompt(10, "ab\n");
            Assert.Equal(2, p.GetLineCount());
        }

        [Fact]
        public void DownArrow_MovesCursorToNextWrappedRow()
        {
            // 20 'a's at width 10: cursor starts at end (row 1, col 10).
            // Home moves to start of logical line (col 0). DownArrow should move to the
            // same column on the second visual row, i.e. index 10.
            var p = MakePrompt(10, new string('a', 20));
            p.OnKey(Key(ConsoleKey.Home));   // cursor -> 0
            p.OnKey(Key(ConsoleKey.DownArrow)); // -> index 10 (start of 2nd visual row)
            p.OnKey(Char('X'));
            Assert.Equal(new string('a', 10) + "X" + new string('a', 10), p.Text);
        }

        [Fact]
        public void UpArrow_MovesCursorToPreviousWrappedRow()
        {
            // 20 'a's, cursor at end (row1). Up should go to row0 same column.
            var p = MakePrompt(10, new string('a', 20)); // cursor at 20
            p.OnKey(Key(ConsoleKey.UpArrow)); // from (row1,col10) -> (row0,col10) => index 10
            p.OnKey(Char('X'));
            Assert.Equal(new string('a', 10) + "X" + new string('a', 10), p.Text);
        }

        [Fact]
        public void DownThenUp_ReturnsToSameVisualColumn()
        {
            var p = MakePrompt(10, new string('a', 25)); // 3 rows
            p.OnKey(Key(ConsoleKey.Home)); // -> 0
            // move right 3 to col 3
            p.OnKey(Key(ConsoleKey.RightArrow));
            p.OnKey(Key(ConsoleKey.RightArrow));
            p.OnKey(Key(ConsoleKey.RightArrow)); // index 3, row0 col3
            p.OnKey(Key(ConsoleKey.DownArrow)); // row1 col3 => index 13
            p.OnKey(Char('X'));
            Assert.Equal(new string('a', 13) + "X" + new string('a', 12), p.Text);
        }

        [Fact]
        public void InsertMidLine_RewrapsAndKeepsContent()
        {
            var p = MakePrompt(10, new string('a', 10)); // exactly 1 row
            // cursor at end (10). Insert one char -> 11 chars => 2 rows.
            p.OnKey(Char('b'));
            Assert.Equal(2, p.GetLineCount());
            Assert.Equal(new string('a', 10) + "b", p.Text);
        }

        [Fact]
        public void Backspace_AcrossWrapBoundary_Rewraps()
        {
            var p = MakePrompt(10, new string('a', 11)); // 2 rows
            p.OnKey(Key(ConsoleKey.Backspace)); // -> 10 chars, 1 row
            Assert.Equal(1, p.GetLineCount());
            Assert.Equal(new string('a', 10), p.Text);
        }

        [Fact]
        public void DownArrow_AtLastRow_DoesNotMove()
        {
            var p = MakePrompt(10, new string('a', 5)); // single row, cursor at end
            p.OnKey(Key(ConsoleKey.DownArrow));
            p.OnKey(Char('X'));
            Assert.Equal(new string('a', 5) + "X", p.Text);
        }

        [Fact]
        public void UpArrow_AtFirstRow_DoesNotMove()
        {
            var p = MakePrompt(10, "abc");
            p.OnKey(Key(ConsoleKey.Home));
            p.OnKey(Key(ConsoleKey.UpArrow));
            p.OnKey(Char('X'));
            Assert.Equal("Xabc", p.Text);
        }

        [Fact]
        public void DownArrow_ToShorterRow_ClampsColumn()
        {
            // Row0 has 10 chars, row1 has 3 chars (logical newline). Cursor at row0 col8.
            // Down should clamp to end of row1 (col3).
            var p = MakePrompt(10, new string('a', 10) + "\n" + "bbb");
            p.OnKey(Key(ConsoleKey.Home)); // start of "bbb" line (cursor was at end)
            // Now cursor at start of "bbb" (index 11). Go up to row0 col0, then right 8.
            p.OnKey(Key(ConsoleKey.UpArrow)); // row0 col0
            for (int i = 0; i < 8; i++) p.OnKey(Key(ConsoleKey.RightArrow)); // row0 col8
            p.OnKey(Key(ConsoleKey.DownArrow)); // row1, clamp col to 3 => end of "bbb"
            p.OnKey(Char('X'));
            Assert.Equal(new string('a', 10) + "\n" + "bbbX", p.Text);
        }
    }
}
