using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Markdown shown in the feed must not contain runs of blank lines, and headings must
/// have a single blank line before them.
/// </summary>
public class FeedMarkdownTests
{
    [Fact]
    public void CollapsesRunsOfBlankLinesToOne()
    {
        Assert.Equal("a\n\nb", FeedMarkdown.Normalize("a\n\n\n\nb"));
        Assert.Equal("a\n\nb", FeedMarkdown.Normalize("a\n\n\nb"));
        Assert.Equal("a\n\nb", FeedMarkdown.Normalize("a\n\nb"));
    }

    [Fact]
    public void CollapsesWhitespaceOnlyLines()
    {
        Assert.Equal("a\n\nb", FeedMarkdown.Normalize("a\n  \n\t\n   \nb"));
    }

    [Fact]
    public void NormalizesCrlfAndTrimsLeadingTrailingBlanks()
    {
        Assert.Equal("a\n\nb", FeedMarkdown.Normalize("\r\n\r\na\r\n\r\n\r\nb\r\n\r\n"));
    }

    [Fact]
    public void InsertsBlankLineBeforeHeadings()
    {
        Assert.Equal("text\n\n## Heading", FeedMarkdown.Normalize("text\n## Heading"));
        Assert.Equal("text\n\n# H1\n\n## H2", FeedMarkdown.Normalize("text\n# H1\n## H2"));
    }

    [Fact]
    public void DoesNotDoubleBlankBeforeHeadingWhenAlreadyPresent()
    {
        Assert.Equal("text\n\n## Heading", FeedMarkdown.Normalize("text\n\n\n## Heading"));
    }

    [Fact]
    public void DoesNotAddLeadingBlankWhenHeadingStartsContent()
    {
        // A blank line is inserted only BEFORE a heading, not after it, and never at the
        // very start of the content.
        Assert.Equal("# Title\nbody", FeedMarkdown.Normalize("# Title\nbody"));
    }

    [Fact]
    public void EmptyInputStaysEmpty()
    {
        Assert.Equal(string.Empty, FeedMarkdown.Normalize(""));
        Assert.Equal(string.Empty, FeedMarkdown.Normalize("\n\n\n"));
    }

    [Theory]
    [InlineData("## Summary:", "## Summary")]
    [InlineData("### Details;", "### Details")]
    [InlineData("# Title ;", "# Title")]
    [InlineData("## Done ::;", "## Done")]
    public void StripsTrailingColonAndSemicolonFromHeadings(string input, string expected)
        => Assert.Equal(expected, FeedMarkdown.Normalize(input));

    [Fact]
    public void DoesNotStripPunctuationFromBodyLines()
    {
        // Only headings are stripped; ordinary text keeps its punctuation.
        Assert.Equal("note: this stays;", FeedMarkdown.Normalize("note: this stays;"));
    }

    [Fact]
    public void KeepsInternalHeadingColons()
    {
        // Only trailing punctuation is removed.
        Assert.Equal("## A: B", FeedMarkdown.Normalize("## A: B"));
    }
}
