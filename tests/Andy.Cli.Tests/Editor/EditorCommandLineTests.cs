using System.Linq;
using System.Runtime.InteropServices;
using Andy.Cli.Editor;
using Xunit;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// Pins the documented VISUAL/EDITOR grammar (issue #287). The value is split by
/// <see cref="EditorCommandLine"/> and handed to Process.Start with UseShellExecute=false,
/// so anything this parser does NOT do (expansion, word splitting inside quotes, pipelines)
/// can never reach a shell.
/// </summary>
public class EditorCommandLineTests
{
    private static (string File, string[] Args) Parse(string value)
    {
        Assert.True(EditorCommandLine.TryParse(value, out var file, out var args, out var error), error);
        return (file, args.ToArray());
    }

    [Fact]
    public void BareProgram_HasNoArguments()
    {
        var (file, args) = Parse("vim");
        Assert.Equal("vim", file);
        Assert.Empty(args);
    }

    [Fact]
    public void CodeWait_SplitsIntoProgramAndFlag()
    {
        var (file, args) = Parse("code --wait");
        Assert.Equal("code", file);
        Assert.Equal(new[] { "--wait" }, args);
    }

    [Fact]
    public void ExtraWhitespace_IsCollapsed()
    {
        var (file, args) = Parse("  code \t --wait   -n  ");
        Assert.Equal("code", file);
        Assert.Equal(new[] { "--wait", "-n" }, args);
    }

    [Fact]
    public void DoubleQuotedProgramPathWithSpaces_StaysOneToken()
    {
        var (file, args) = Parse("\"/Applications/My Editor/bin/edit\" --wait");
        Assert.Equal("/Applications/My Editor/bin/edit", file);
        Assert.Equal(new[] { "--wait" }, args);
    }

    [Fact]
    public void SingleQuotedArgumentWithSpaces_StaysOneToken()
    {
        var (file, args) = Parse("nvim -c 'set wrap linebreak'");
        Assert.Equal("nvim", file);
        Assert.Equal(new[] { "-c", "set wrap linebreak" }, args);
    }

    [Fact]
    public void EmptySingleQuotes_ProduceAnEmptyArgument()
    {
        // emacsclient -a '' is the documented "start a daemon if none is running" form.
        var (file, args) = Parse("emacsclient -nw -a ''");
        Assert.Equal("emacsclient", file);
        Assert.Equal(new[] { "-nw", "-a", "" }, args);
    }

    [Fact]
    public void DoubleQuotes_HonorEscapedQuoteAndBackslash()
    {
        var (_, args) = Parse("ed \"a\\\"b\" \"c\\\\d\"");
        Assert.Equal(new[] { "a\"b", "c\\d" }, args);
    }

    [Fact]
    public void SingleQuotes_TakeContentsLiterally()
    {
        var (_, args) = Parse("ed '\\n$HOME\"'");
        Assert.Equal(new[] { "\\n$HOME\"" }, args);
    }

    [Fact]
    public void ShellMetacharacters_ArePassedThroughLiterally_NeverExpanded()
    {
        // No shell is ever involved, so these stay exactly as written.
        var (file, args) = Parse("ed $HOME ~/notes *.md 'a|b' 'x;y' 'p&&q' '>out'");
        Assert.Equal("ed", file);
        Assert.Equal(new[] { "$HOME", "~/notes", "*.md", "a|b", "x;y", "p&&q", ">out" }, args);
    }

    [Fact]
    public void UnterminatedDoubleQuote_IsAnError()
    {
        Assert.False(EditorCommandLine.TryParse("\"/opt/my editor --wait", out _, out _, out var error));
        Assert.Contains("double quote", error);
    }

    [Fact]
    public void UnterminatedSingleQuote_IsAnError()
    {
        Assert.False(EditorCommandLine.TryParse("ed 'oops", out _, out _, out var error));
        Assert.Contains("single quote", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void BlankValues_AreAnError(string? value)
    {
        Assert.False(EditorCommandLine.TryParse(value, out _, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void BackslashEscape_IsUnixOnly()
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        Assert.True(EditorCommandLine.TryParse("/opt/my\\ editor --wait", out var file, out var args, out _));
        if (windows)
        {
            // On Windows a backslash is a path separator, so it stays literal and the value splits.
            Assert.Equal("/opt/my\\", file);
            Assert.Equal(new[] { "editor", "--wait" }, args.ToArray());
        }
        else
        {
            Assert.Equal("/opt/my editor", file);
            Assert.Equal(new[] { "--wait" }, args.ToArray());
        }
    }

    [Fact]
    public void UnquotedPathWithSpaces_SplitsIntoSeparateTokens()
    {
        // Documented failure mode: without quotes the user gets a launch failure rather
        // than a surprise program. The composer is left untouched (see ExternalEditorServiceTests).
        var (file, args) = Parse("/Applications/My Editor/bin/edit --wait");
        Assert.Equal("/Applications/My", file);
        Assert.Equal(new[] { "Editor/bin/edit", "--wait" }, args);
    }
}
