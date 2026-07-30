using System.Linq;
using Andy.Cli.Services.FileMentions;
using Xunit;

namespace Andy.Cli.Tests.Services.FileMentions;

public class FileMentionSyntaxTests
{
    [Fact]
    public void TryFindMentionAtCursor_CursorInsideMention_FindsToken()
    {
        const string text = "look at @src/Foo.cs please";
        int cursor = text.IndexOf("src/Foo.cs") + 3;

        Assert.True(FileMentionSyntax.TryFindMentionAtCursor(text, cursor, out var token));
        Assert.Equal(8, token.Start);
        Assert.Equal(text.IndexOf(" please"), token.End);
    }

    [Fact]
    public void TryFindMentionAtCursor_CursorBeforeMention_DoesNotTrigger()
    {
        const string text = "look at @src/Foo.cs";
        Assert.False(FileMentionSyntax.TryFindMentionAtCursor(text, 8, out _));
    }

    [Fact]
    public void TryFindMentionAtCursor_CursorImmediatelyAfterAt_Triggers()
    {
        const string text = "look at @src/Foo.cs";
        Assert.True(FileMentionSyntax.TryFindMentionAtCursor(text, 9, out var token));
        Assert.Equal(8, token.Start);
    }

    [Fact]
    public void TryFindMentionAtCursor_CursorAtEndOfMention_Triggers()
    {
        const string text = "look at @src/Foo.cs";
        Assert.True(FileMentionSyntax.TryFindMentionAtCursor(text, text.Length, out var token));
        Assert.Equal(8, token.Start);
        Assert.Equal(text.Length, token.End);
    }

    [Fact]
    public void TryFindMentionAtCursor_CursorAfterMentionAndSpace_DoesNotTrigger()
    {
        const string text = "look at @src/Foo.cs now";
        Assert.False(FileMentionSyntax.TryFindMentionAtCursor(text, text.Length, out _));
    }

    [Fact]
    public void TryFindMentionAtCursor_EmailAddress_DoesNotTrigger()
    {
        const string text = "mail sam@rivoli.ai";
        Assert.False(FileMentionSyntax.TryFindMentionAtCursor(text, text.Length, out _));
    }

    [Fact]
    public void TryFindMentionAtCursor_OnSecondLineOfMultilineText_UsesThatLine()
    {
        const string text = "first line\nsecond @src/Bar.cs";
        Assert.True(FileMentionSyntax.TryFindMentionAtCursor(text, text.Length, out var token));
        Assert.Equal(text.IndexOf('@'), token.Start);
    }

    [Fact]
    public void TryFindMentionAtCursor_CursorOnLineWithoutMention_DoesNotTrigger()
    {
        const string text = "@src/Foo.cs\nsecond line";
        Assert.False(FileMentionSyntax.TryFindMentionAtCursor(text, text.Length, out _));
    }

    [Fact]
    public void TryFindMentionAtCursor_BareAtSign_TriggersWithEmptyQuery()
    {
        const string text = "tell me about @";
        Assert.True(FileMentionSyntax.TryFindMentionAtCursor(text, text.Length, out var token));
        var (query, range) = FileMentionSyntax.QueryUpToCursor(text, token, text.Length);
        Assert.Equal(string.Empty, query);
        Assert.Null(range);
    }

    [Fact]
    public void QueryUpToCursor_ReturnsOnlyTextBeforeCursor()
    {
        const string text = "@src/Foo.cs";
        var (query, _) = FileMentionSyntax.QueryUpToCursor(text, new MentionToken(0, text.Length), 5);
        Assert.Equal("src/", query);
    }

    [Fact]
    public void QueryUpToCursor_WithWindowsSeparators_NormalizesToForwardSlashes()
    {
        const string text = @"@src\Andy.Cli\Program";
        Assert.True(FileMentionSyntax.TryFindMentionAtCursor(text, text.Length, out var token));
        var (query, _) = FileMentionSyntax.QueryUpToCursor(text, token, text.Length);
        Assert.Equal("src/Andy.Cli/Program", query);
    }

    [Fact]
    public void QueryUpToCursor_WithPartialRange_SeparatesQueryFromRange()
    {
        const string text = "@src/Foo.cs#L10-L20";
        Assert.True(FileMentionSyntax.TryFindMentionAtCursor(text, text.Length, out var token));
        var (query, range) = FileMentionSyntax.QueryUpToCursor(text, token, text.Length);
        Assert.Equal("src/Foo.cs", query);
        Assert.Equal(new LineRange(10, 20), range);
    }

    [Fact]
    public void FindAll_ReturnsEveryMentionInOrder()
    {
        const string text = "compare @a.cs with @b.cs and @c.cs";
        var tokens = FileMentionSyntax.FindAll(text);
        Assert.Equal(3, tokens.Count);
        Assert.Equal(new[] { "@a.cs", "@b.cs", "@c.cs" },
            tokens.Select(t => text.Substring(t.Start, t.Length)).ToArray());
    }

    [Fact]
    public void FindAll_SkipsAtSignsInsideWords()
    {
        const string text = "user@example.com and @real.cs";
        var tokens = FileMentionSyntax.FindAll(text);
        Assert.Single(tokens);
        Assert.Equal("@real.cs", text.Substring(tokens[0].Start, tokens[0].Length));
    }

    [Fact]
    public void FindAll_QuotedMentionWithSpaces_IsOneToken()
    {
        const string text = "read @\"docs/my notes.md\" then stop";
        var tokens = FileMentionSyntax.FindAll(text);
        Assert.Single(tokens);
        Assert.Equal("@\"docs/my notes.md\"", text.Substring(tokens[0].Start, tokens[0].Length));
    }

    [Theory]
    [InlineData("src/Foo.cs#L12-L40", "src/Foo.cs", 12, 40)]
    [InlineData("src/Foo.cs#12-40", "src/Foo.cs", 12, 40)]
    [InlineData("src/Foo.cs#L12", "src/Foo.cs", 12, 12)]
    [InlineData("src/Foo.cs#7", "src/Foo.cs", 7, 7)]
    [InlineData("src/Foo.cs#40-12", "src/Foo.cs", 12, 40)]
    public void SplitBody_ParsesLineRanges(string body, string expectedPath, int start, int end)
    {
        var (path, range, _) = FileMentionSyntax.SplitBody(body);
        Assert.Equal(expectedPath, path);
        Assert.Equal(new LineRange(start, end), range);
    }

    [Theory]
    [InlineData("notes#draft.md")]
    [InlineData("issue#abc")]
    [InlineData("weird#")]
    public void SplitBody_NonRangeHashSuffix_StaysPartOfThePath(string body)
    {
        var (path, range, _) = FileMentionSyntax.SplitBody(body);
        Assert.Equal(body, path);
        Assert.Null(range);
    }

    [Fact]
    public void SplitBody_QuotedPathWithHash_KeepsHashInPath()
    {
        var (path, range, _) = FileMentionSyntax.SplitBody("\"docs/rev#12.md\"");
        Assert.Equal("docs/rev#12.md", path);
        Assert.Null(range);
    }

    [Fact]
    public void SplitBody_QuotedPathWithTrailingRange_ParsesBoth()
    {
        var (path, range, _) = FileMentionSyntax.SplitBody("\"docs/my notes.md\"#L3-L9");
        Assert.Equal("docs/my notes.md", path);
        Assert.Equal(new LineRange(3, 9), range);
    }

    [Fact]
    public void SplitBody_UnicodePath_IsPreserved()
    {
        var (path, range, _) = FileMentionSyntax.SplitBody("docs/café/日本語.md");
        Assert.Equal("docs/café/日本語.md", path);
        Assert.Null(range);
    }

    [Fact]
    public void SplitBody_LeadingDotSlash_IsStripped()
    {
        var (path, _, _) = FileMentionSyntax.SplitBody("./src/Foo.cs");
        Assert.Equal("src/Foo.cs", path);
    }

    [Theory]
    [InlineData("src/Foo.cs", false, "@src/Foo.cs")]
    [InlineData("docs/my notes.md", false, "@\"docs/my notes.md\"")]
    [InlineData("docs/rev#12.md", false, "@\"docs/rev#12.md\"")]
    [InlineData("src", true, "@src/")]
    public void FormatMention_QuotesOnlyWhenNecessary(string path, bool isDirectory, string expected)
    {
        Assert.Equal(expected, FileMentionSyntax.FormatMention(path, isDirectory));
    }

    [Fact]
    public void FormatMention_WithRange_AppendsSuffix()
    {
        Assert.Equal("@src/Foo.cs#L4-L8", FileMentionSyntax.FormatMention("src/Foo.cs", false, new LineRange(4, 8)));
        Assert.Equal("@src/Foo.cs#L4", FileMentionSyntax.FormatMention("src/Foo.cs", false, new LineRange(4, 4)));
    }

    [Fact]
    public void FormatMention_RoundTripsPathsWithSpacesAndHashes()
    {
        const string path = "docs/a b#3.md";
        string mention = FileMentionSyntax.FormatMention(path);
        var (parsed, range, _) = FileMentionSyntax.SplitBody(mention.Substring(1));
        Assert.Equal(path, parsed);
        Assert.Null(range);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("L0")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1-")]
    public void TryParseRange_RejectsInvalidSuffixes(string suffix)
    {
        Assert.False(FileMentionSyntax.TryParseRange(suffix, out _));
    }
}
