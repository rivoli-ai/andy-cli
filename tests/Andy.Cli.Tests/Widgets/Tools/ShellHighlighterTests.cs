using System.Linq;
using Andy.Cli.Themes;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Shell command highlighting (#247, #251). CodeHighlighter's rules are wrong here - it would
/// call a PascalCase word a type and any word before "(" a method call - so shell lines get
/// their own tokenizer.
/// </summary>
public class ShellHighlighterTests
{
    private static StyledSpan SpanFor(string command, string token)
        => ShellHighlighter.Highlight(command).Spans.First(s => s.Text == token);

    [Fact]
    public void TextIsPreservedExactly()
    {
        const string command = "grep -rn \"foo bar\" src/ | head -20";

        Assert.Equal(command, ShellHighlighter.Highlight(command).Text);
    }

    [Fact]
    public void ExecutableIsHighlightedAsATypeName()
    {
        Assert.Equal(Theme.Current.SyntaxType, SpanFor("dotnet build", "dotnet").Foreground);
    }

    [Fact]
    public void FlagsAreHighlightedAsKeywords()
    {
        Assert.Equal(Theme.Current.SyntaxKeyword, SpanFor("ls -la --color", "-la").Foreground);
        Assert.Equal(Theme.Current.SyntaxKeyword, SpanFor("ls -la --color", "--color").Foreground);
    }

    [Fact]
    public void ArgumentsAreLeftUnstyled()
    {
        // A path argument is not a keyword and must not be colored like one.
        Assert.Null(SpanFor("cat src/Program.cs", "src/Program.cs").Foreground);
    }

    [Fact]
    public void QuotedStringsAreOneSpan()
    {
        var span = SpanFor("grep \"hello world\" f.txt", "\"hello world\"");

        Assert.Equal(Theme.Current.SyntaxString, span.Foreground);
    }

    [Fact]
    public void UnterminatedQuoteDoesNotSwallowTheRest()
    {
        // It colors to end of line rather than looping or dropping characters.
        var line = ShellHighlighter.Highlight("echo \"unterminated");

        Assert.Equal("echo \"unterminated", line.Text);
    }

    [Fact]
    public void VariablesAreHighlighted()
    {
        Assert.Equal(Theme.Current.SyntaxType, SpanFor("echo $HOME", "$HOME").Foreground);
        Assert.Equal(Theme.Current.SyntaxType, SpanFor("echo ${PATH}", "${PATH}").Foreground);
    }

    [Fact]
    public void WordAfterAPipeIsTreatedAsANewCommand()
    {
        var spans = ShellHighlighter.Highlight("cat f | grep foo").Spans.ToList();

        Assert.Equal(Theme.Current.SyntaxType, spans.First(s => s.Text == "cat").Foreground);
        Assert.Equal(Theme.Current.SyntaxType, spans.First(s => s.Text == "grep").Foreground);
        Assert.Null(spans.First(s => s.Text == "foo").Foreground);
    }

    [Fact]
    public void CommandPrefixesKeepTheNextWordAsTheExecutable()
    {
        var spans = ShellHighlighter.Highlight("sudo dotnet build").Spans.ToList();

        Assert.Equal(Theme.Current.SyntaxType, spans.First(s => s.Text == "sudo").Foreground);
        Assert.Equal(Theme.Current.SyntaxType, spans.First(s => s.Text == "dotnet").Foreground);
        Assert.Null(spans.First(s => s.Text == "build").Foreground);
    }

    [Fact]
    public void CommentsRunToEndOfLine()
    {
        var line = ShellHighlighter.Highlight("make all # rebuild everything");

        Assert.Equal(Theme.Current.SyntaxComment, line.Spans.Last().Foreground);
        Assert.Equal("# rebuild everything", line.Spans.Last().Text);
    }

    [Fact]
    public void HashInsideAWordIsNotAComment()
    {
        // A URL fragment or an anchor must not turn the rest of the command into a comment.
        var line = ShellHighlighter.Highlight("curl https://example.com/page#section");

        Assert.DoesNotContain(line.Spans, s => s.Foreground == Theme.Current.SyntaxComment);
    }

    [Fact]
    public void EmptyCommandIsHandled()
    {
        Assert.True(ShellHighlighter.Highlight("").IsEmpty);
        Assert.True(ShellHighlighter.Highlight(null).IsEmpty);
    }
}
