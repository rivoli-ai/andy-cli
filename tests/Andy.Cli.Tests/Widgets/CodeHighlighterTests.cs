using System.Linq;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class CodeHighlighterTests
{
    private static SyntaxKind KindOf(string line, string lang, string token) =>
        CodeHighlighter.Tokenize(line, lang).First(t => t.Text == token).Kind;

    [Fact]
    public void ClassifiesCSharpTokens()
    {
        var toks = CodeHighlighter.Tokenize("public void Foo() { return 42; } // hi", "csharp");
        Assert.Equal(SyntaxKind.Keyword, toks.First(t => t.Text == "public").Kind);
        Assert.Equal(SyntaxKind.Keyword, toks.First(t => t.Text == "return").Kind);
        Assert.Equal(SyntaxKind.Number, toks.First(t => t.Text == "42").Kind);
        Assert.Equal(SyntaxKind.Comment, toks.First(t => t.Text.StartsWith("//")).Kind);
    }

    [Fact]
    public void MethodCallsAndClassNamesAreTypeColored()
    {
        // PascalCase identifier => type/class name; identifier before '(' => method call.
        Assert.Equal(SyntaxKind.Type, KindOf("var x = new Widget();", "cs", "Widget"));
        Assert.Equal(SyntaxKind.Type, KindOf("result = compute(a, b);", "cs", "compute"));
        Assert.Equal(SyntaxKind.Identifier, KindOf("result = compute(a, b);", "cs", "result"));
    }

    [Fact]
    public void StringsAreOneToken()
    {
        var toks = CodeHighlighter.Tokenize("name = \"hello world\";", "cs");
        Assert.Contains(toks, t => t.Kind == SyntaxKind.String && t.Text == "\"hello world\"");
    }

    [Fact]
    public void PythonCommentsAndKeywords()
    {
        var toks = CodeHighlighter.Tokenize("def foo():  # comment", "python");
        Assert.Equal(SyntaxKind.Keyword, toks.First(t => t.Text == "def").Kind);
        Assert.Equal(SyntaxKind.Comment, toks.First(t => t.Text.StartsWith("#")).Kind);
    }

    [Fact]
    public void NoTokenUsesUnderline_PaletteMapsToColors()
    {
        // The palette maps every kind to a distinct color; highlighting never returns attrs.
        var p = new SyntaxPalette(
            keyword: new Andy.Tui.DisplayList.Rgb24(1, 0, 0),
            type: new Andy.Tui.DisplayList.Rgb24(2, 0, 0),
            str: new Andy.Tui.DisplayList.Rgb24(3, 0, 0),
            comment: new Andy.Tui.DisplayList.Rgb24(4, 0, 0),
            number: new Andy.Tui.DisplayList.Rgb24(5, 0, 0),
            identifier: new Andy.Tui.DisplayList.Rgb24(6, 0, 0),
            def: new Andy.Tui.DisplayList.Rgb24(7, 0, 0));
        var spans = CodeHighlighter.Highlight("class A { }", "cs", p).ToList();
        Assert.Equal(new Andy.Tui.DisplayList.Rgb24(1, 0, 0), spans.First(s => s.Text == "class").Color);
        Assert.Equal(new Andy.Tui.DisplayList.Rgb24(2, 0, 0), spans.First(s => s.Text == "A").Color);
    }
}
