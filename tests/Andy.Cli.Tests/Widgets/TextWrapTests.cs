using System.Linq;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class TextWrapTests
{
    [Fact]
    public void ShortText_StaysOnOneLine()
    {
        var lines = TextWrap.Wrap("short", 20);
        Assert.Equal(new[] { "short" }, lines);
    }

    [Fact]
    public void WordWrapsAtSpaces()
    {
        var lines = TextWrap.Wrap("the quick brown fox", 9);
        Assert.All(lines, l => Assert.True(l.Length <= 9, $"'{l}' exceeds width"));
        Assert.Equal("the quick brown fox", string.Join(" ", lines));
    }

    [Fact]
    public void HardBreaksLongTokensSoNothingIsTruncated()
    {
        // A long path/command with no spaces must still be fully visible across lines.
        var path = "/Users/sam/Devel/some/really/long/path/to/a/file-with-a-long-name.txt";
        var lines = TextWrap.Wrap(path, 16);
        Assert.All(lines, l => Assert.True(l.Length <= 16));
        Assert.Equal(path, string.Concat(lines)); // reassembling loses nothing
    }

    [Fact]
    public void PreservesExistingLineBreaksAndBlankLines()
    {
        var lines = TextWrap.Wrap("a\n\nb", 20);
        Assert.Equal(new[] { "a", "", "b" }, lines);
    }

    [Fact]
    public void HandlesNullAndEmpty()
    {
        Assert.Equal(new[] { "" }, TextWrap.Wrap("", 10));
        Assert.Equal(new[] { "" }, TextWrap.Wrap(null!, 10));
    }
}
