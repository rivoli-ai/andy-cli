using System;
using System.IO;
using System.Linq;
using Andy.Cli.Commands;
using Andy.Cli.Editor;
using Xunit;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// The documented surface of the external editor (issue #287): the slash command is
/// registered, the key binding is documented, and docs/external-editor.md carries the
/// per-editor examples the acceptance criteria ask for.
/// </summary>
public class ExternalEditorDocsTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Andy.Cli.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string DocsText()
        => File.ReadAllText(Path.Combine(RepositoryRoot(), EditorSetupGuidance.DocsPath));

    [Fact]
    public void SlashCatalog_RegistersEditor()
    {
        var editor = Assert.Single(SlashCommandCatalog.CreateInlineHelpCommands(), c => c.Name == "editor");

        Assert.Contains("edit", editor.Aliases);
        Assert.Contains("VISUAL", editor.Description);
        Assert.Contains("EDITOR", editor.Description);
    }

    [Fact]
    public void InteractiveHelp_DocumentsTheCommandAndTheKeyBinding()
    {
        var help = HelpText.InteractiveHelpMarkdown();

        Assert.Contains("/editor", help);
        Assert.Contains("Ctrl+X", help);
        Assert.Contains("VISUAL", help);
        Assert.Contains(EditorSetupGuidance.DocsPath, help);
    }

    [Fact]
    public void DocsFile_Exists()
        => Assert.True(File.Exists(Path.Combine(RepositoryRoot(), EditorSetupGuidance.DocsPath)));

    [Theory]
    [InlineData("Vim")]
    [InlineData("Neovim")]
    [InlineData("VS Code")]
    [InlineData("Nano")]
    [InlineData("Micro")]
    [InlineData("Helix")]
    [InlineData("Emacs")]
    [InlineData("Sublime Text")]
    public void Docs_IncludeAnExampleForEachCommonEditor(string editor)
        => Assert.Contains(editor, DocsText());

    [Theory]
    [InlineData("export VISUAL='vim'")]
    [InlineData("export VISUAL='nvim'")]
    [InlineData("export VISUAL='code --wait'")]
    [InlineData("export VISUAL='nano'")]
    [InlineData("export VISUAL='hx'")]
    [InlineData("export VISUAL='emacs -nw'")]
    public void Docs_ShowTheExactValueToExport(string example)
        => Assert.Contains(example, DocsText());

    [Fact]
    public void Docs_CoverTheKeyBindingTheCommandAndTheWaitCaveat()
    {
        var docs = DocsText();

        Assert.Contains("Ctrl+X", docs);
        Assert.Contains("/editor", docs);
        Assert.Contains("--wait", docs);
        Assert.Contains("VISUAL", docs);
        Assert.Contains("EDITOR", docs);
    }

    [Fact]
    public void Docs_CoverPathsWithSpaces_AndTheNoShellRule()
    {
        var docs = DocsText();

        Assert.Contains("spaces", docs);
        Assert.Contains("no shell", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("My Editor", docs);
    }

    [Fact]
    public void Docs_CoverTempFilePermissionsSizeLimitAndStructuredParts()
    {
        var docs = DocsText();

        Assert.Contains("0600", docs);
        Assert.Contains("0700", docs);
        Assert.Contains("1 MiB", docs);
        Assert.Contains("#277", docs);
    }

    [Fact]
    public void Guidance_ListsTheSameEditorsAsTheDocs()
    {
        var docs = DocsText();
        foreach (var (_, value, _) in EditorSetupGuidance.Examples)
            Assert.Contains(value, docs);
    }

    [Fact]
    public void NoEmojiInUserFacingText()
    {
        // Project rule: ASCII-only terminal output.
        foreach (char c in EditorSetupGuidance.NotConfiguredMessage() + EditorSetupGuidance.QuotingHelp())
            Assert.True(c < 0x2190, $"Non-ASCII symbol U+{(int)c:X4} in editor guidance");
    }
}
