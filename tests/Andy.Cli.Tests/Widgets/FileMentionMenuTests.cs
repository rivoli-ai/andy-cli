using System;
using System.Linq;
using Andy.Cli.Services.FileMentions;
using Andy.Cli.Tests.Services.FileMentions;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Tests.Widgets;

public class FileMentionMenuTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private FileMentionMenu Menu()
    {
        var search = new FileMentionSearchService(
            new WorkspaceFileIndex(_workspace.Root, new WorkspaceIgnoreRules(_workspace.Root)));
        return new FileMentionMenu(search);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, bool ctrl = false) =>
        new('\0', key, false, false, ctrl);

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    /// <summary>Type text into a prompt one key at a time, refreshing the menu after each key.</summary>
    private static void Type(PromptLine prompt, FileMentionMenu menu, string text)
    {
        foreach (var c in text)
        {
            prompt.OnKey(Char(c));
            menu.Update(prompt.Text, prompt.CursorPosition);
        }
    }

    [Fact]
    public void Update_WithoutMention_KeepsMenuClosed()
    {
        var menu = Menu();
        menu.Update("just some text", 14);

        Assert.False(menu.IsOpen);
        Assert.Equal(0, menu.GetHeight());
    }

    [Fact]
    public void Update_ResultsNarrowAsTheUserTypes()
    {
        _workspace.WriteFile("src/Program.cs", "x");
        _workspace.WriteFile("src/Parser.cs", "x");
        _workspace.WriteFile("docs/readme.md", "x");
        var menu = Menu();
        var prompt = new PromptLine();

        Type(prompt, menu, "@");
        Assert.True(menu.IsOpen);
        int atEverything = menu.Suggestions.Count;

        Type(prompt, menu, "pr");
        Assert.True(menu.Suggestions.Count < atEverything);
        Assert.DoesNotContain(menu.Suggestions, s => s.RelativePath == "docs/readme.md");

        Type(prompt, menu, "ogr");
        Assert.Equal("src/Program.cs", Assert.Single(menu.Suggestions).RelativePath);
    }

    [Fact]
    public void Update_BackspacingWidensResultsAgain()
    {
        _workspace.WriteFile("src/Program.cs", "x");
        _workspace.WriteFile("src/Parser.cs", "x");
        var menu = Menu();
        var prompt = new PromptLine();

        Type(prompt, menu, "@progr");
        Assert.Single(menu.Suggestions);

        for (int i = 0; i < 4; i++)
        {
            prompt.OnKey(Key(ConsoleKey.Backspace));
            menu.Update(prompt.Text, prompt.CursorPosition);
        }

        Assert.Equal("p", menu.Query);
        Assert.True(menu.Suggestions.Count > 1);
    }

    [Fact]
    public void MoveSelection_WrapsAndSurvivesUnrelatedUpdates()
    {
        _workspace.WriteFile("src/Alpha.cs", "x");
        _workspace.WriteFile("src/Beta.cs", "x");
        _workspace.WriteFile("src/Gamma.cs", "x");
        var menu = Menu();
        var prompt = new PromptLine();
        Type(prompt, menu, "@src/");

        Assert.Equal(0, menu.SelectedIndex);
        menu.MoveSelection(1);
        Assert.Equal(1, menu.SelectedIndex);

        // Re-running Update with the same query must not reset the highlight.
        menu.Update(prompt.Text, prompt.CursorPosition);
        Assert.Equal(1, menu.SelectedIndex);

        menu.MoveSelection(-1);
        menu.MoveSelection(-1);
        Assert.Equal(menu.Suggestions.Count - 1, menu.SelectedIndex);
    }

    [Fact]
    public void HandlesKey_OnlyClaimsNavigationKeysWhileOpen()
    {
        _workspace.WriteFile("src/Alpha.cs", "x");
        var menu = Menu();

        Assert.False(menu.HandlesKey(Key(ConsoleKey.Enter)));

        var prompt = new PromptLine();
        Type(prompt, menu, "@alp");

        Assert.True(menu.HandlesKey(Key(ConsoleKey.Enter)));
        Assert.True(menu.HandlesKey(Key(ConsoleKey.Tab)));
        Assert.True(menu.HandlesKey(Key(ConsoleKey.UpArrow)));
        Assert.True(menu.HandlesKey(Key(ConsoleKey.DownArrow)));
        Assert.True(menu.HandlesKey(Key(ConsoleKey.Escape)));
        Assert.False(menu.HandlesKey(Key(ConsoleKey.LeftArrow)));
        Assert.False(menu.HandlesKey(Key(ConsoleKey.Enter, ctrl: true)));
        Assert.False(menu.HandlesKey(Key(ConsoleKey.UpArrow, ctrl: true)));
    }

    [Fact]
    public void Dismiss_ClosesUntilTheQueryChanges()
    {
        _workspace.WriteFile("src/Alpha.cs", "x");
        var menu = Menu();
        var prompt = new PromptLine();
        Type(prompt, menu, "@alp");
        Assert.True(menu.IsOpen);

        menu.Dismiss();
        menu.Update(prompt.Text, prompt.CursorPosition);
        Assert.False(menu.IsOpen);

        Type(prompt, menu, "h");
        Assert.True(menu.IsOpen);
    }

    [Fact]
    public void BuildCompletion_ReplacesOnlyTheMentionToken()
    {
        _workspace.WriteFile("src/Program.cs", "x");
        var menu = Menu();
        var prompt = new PromptLine();
        Type(prompt, menu, "please read @progr");

        var completion = menu.BuildCompletion();
        Assert.NotNull(completion);
        prompt.ReplaceRange(completion!.Value.Start, completion.Value.Length, completion.Value.Text, completion.Value.NewCursor);

        Assert.Equal("please read @src/Program.cs ", prompt.Text);
        Assert.Equal(prompt.Text.Length, prompt.CursorPosition);
    }

    [Fact]
    public void BuildCompletion_InTheMiddleOfMultilineText_LeavesTheRestIntact()
    {
        _workspace.WriteFile("src/Program.cs", "x");
        var menu = Menu();
        var prompt = new PromptLine();
        prompt.SetText("first line\nsecond @progr\nthird line");

        int cursor = "first line\nsecond @progr".Length;
        menu.Update(prompt.Text, cursor);
        Assert.True(menu.IsOpen);

        var completion = menu.BuildCompletion()!.Value;
        prompt.ReplaceRange(completion.Start, completion.Length, completion.Text, completion.NewCursor);

        Assert.Equal("first line\nsecond @src/Program.cs \nthird line", prompt.Text);
        Assert.Equal("first line\nsecond @src/Program.cs ".Length, prompt.CursorPosition);

        // The caret is still on the second line, and typing lands there.
        prompt.OnKey(Char('X'));
        Assert.Equal("first line\nsecond @src/Program.cs X\nthird line", prompt.Text);
    }

    [Fact]
    public void BuildCompletion_KeepsAnAlreadyTypedLineRange()
    {
        _workspace.WriteFile("src/Program.cs", "x");
        var menu = Menu();
        var prompt = new PromptLine();
        Type(prompt, menu, "@progr#L10-L20");

        var completion = menu.BuildCompletion()!.Value;
        prompt.ReplaceRange(completion.Start, completion.Length, completion.Text, completion.NewCursor);

        Assert.Equal("@src/Program.cs#L10-L20 ", prompt.Text);
    }

    [Fact]
    public void BuildCompletion_QuotesPathsWithSpaces()
    {
        _workspace.WriteFile("docs/my notes.md", "x");
        var menu = Menu();
        var prompt = new PromptLine();
        Type(prompt, menu, "@mynotes");

        var completion = menu.BuildCompletion()!.Value;
        prompt.ReplaceRange(completion.Start, completion.Length, completion.Text, completion.NewCursor);

        Assert.Equal("@\"docs/my notes.md\" ", prompt.Text);
    }

    [Fact]
    public void BuildCompletion_ForDirectory_KeepsThePickerOpenForDrillDown()
    {
        _workspace.WriteFile("widgets/Feed.cs", "x");
        var menu = Menu();
        var prompt = new PromptLine();
        Type(prompt, menu, "@widgets");

        var directory = menu.Suggestions.ToList().FindIndex(s => s.IsDirectory);
        Assert.True(directory >= 0);
        menu.MoveSelection(directory - menu.SelectedIndex);

        var completion = menu.BuildCompletion()!.Value;
        prompt.ReplaceRange(completion.Start, completion.Length, completion.Text, completion.NewCursor);
        menu.Update(prompt.Text, prompt.CursorPosition);

        Assert.Equal("@widgets/", prompt.Text);
        Assert.True(menu.IsOpen);
        Assert.Equal("widgets/", menu.Query);
    }

    [Fact]
    public void Update_CursorBeforeWithinAndAfterAMention()
    {
        _workspace.WriteFile("src/Program.cs", "x");
        var menu = Menu();
        const string text = "read @src/Program.cs now";
        int mentionStart = text.IndexOf('@');

        menu.Update(text, mentionStart);
        Assert.False(menu.IsOpen);

        menu.Update(text, mentionStart + 4);
        Assert.True(menu.IsOpen);
        Assert.Equal("src", menu.Query);

        menu.Update(text, text.IndexOf(" now", StringComparison.Ordinal) + 1);
        Assert.False(menu.IsOpen);

        menu.Update(text, text.Length);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void RecordAccepted_FeedsFrecencyRanking()
    {
        _workspace.WriteFile("alpha/Item.cs", "x");
        _workspace.WriteFile("bravo/Item.cs", "x");
        var frecency = new FrecencyStore();
        var search = new FileMentionSearchService(
            new WorkspaceFileIndex(_workspace.Root, new WorkspaceIgnoreRules(_workspace.Root)), frecency);
        var menu = new FileMentionMenu(search);
        var prompt = new PromptLine();

        Type(prompt, menu, "@item.cs");
        menu.MoveSelection(1);
        string second = menu.Selected!.RelativePath;
        menu.RecordAccepted();

        Assert.True(frecency.GetBonus(second) > 0);
        Assert.Equal(second, search.Search("item.cs")[0].RelativePath);
    }

    [Fact]
    public void GetHeight_TracksVisibleSuggestionCount()
    {
        for (int i = 0; i < 12; i++)
        {
            _workspace.WriteFile($"item{i}.txt", "x");
        }
        var menu = Menu();
        var prompt = new PromptLine();
        Type(prompt, menu, "@item");

        Assert.Equal(FileMentionMenu.MaxDisplayLines + 2, menu.GetHeight());
    }

    [Fact]
    public void Render_DrawsTheHighlightedSuggestion()
    {
        _workspace.WriteFile("src/Program.cs", "x");
        var menu = Menu();
        var prompt = new PromptLine();
        Type(prompt, menu, "@progr");

        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        builder.PushClip(new DL.ClipPush(0, 0, 60, 10));
        menu.Render(0, 0, 60, baseDl, builder);
        builder.Pop();
        var displayList = builder.Build();

        Assert.Contains(displayList.Ops.OfType<DL.TextRun>(), r => r.Content.Contains("Program.cs"));
        Assert.Contains(displayList.Ops.OfType<DL.TextRun>(), r => r.Content.StartsWith("> ", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_WhenClosed_DrawsNothing()
    {
        var menu = Menu();
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        builder.PushClip(new DL.ClipPush(0, 0, 60, 10));
        menu.Render(0, 0, 60, baseDl, builder);
        builder.Pop();

        Assert.DoesNotContain(builder.Build().Ops.OfType<DL.TextRun>(), r => r.Content.Contains("@file"));
    }
}
