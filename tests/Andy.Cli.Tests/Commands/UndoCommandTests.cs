using System;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Services.Undo;
using Andy.Cli.Tests.Services.Undo;
using Xunit;

namespace Andy.Cli.Tests.Commands;

/// <summary>Slash-command surface for the undo/redo transaction log (issue #276).</summary>
public class UndoCommandTests
{
    [Fact]
    public void Constructors_RejectNullManager()
    {
        Assert.Throws<ArgumentNullException>(() => new UndoCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new RedoCommand(null!));
    }

    [Fact]
    public void Metadata_IsRegisteredAndPlainAscii()
    {
        using var workspace = TempGitWorkspace.CreatePlainDirectory();
        using var manager = UndoManager.Create(workspace.WorkspacePath, "s", workspace.SnapshotRoot);
        var undo = new UndoCommand(manager);
        var redo = new RedoCommand(manager);

        Assert.Equal("undo", undo.Name);
        Assert.Equal("redo", redo.Name);
        Assert.Empty(undo.Aliases);
        Assert.Empty(redo.Aliases);
        Assert.IsAssignableFrom<ICommand>(undo);
        Assert.IsAssignableFrom<ICommand>(redo);
        Assert.All(undo.Description, ch => Assert.True(ch <= 127));
        Assert.All(redo.Description, ch => Assert.True(ch <= 127));
    }

    [Fact]
    public async Task Undo_WithArguments_FailsWithUsage()
    {
        using var workspace = TempGitWorkspace.CreatePlainDirectory();
        using var manager = UndoManager.Create(workspace.WorkspacePath, "s", workspace.SnapshotRoot);

        var undo = await new UndoCommand(manager).ExecuteAsync(new[] { "2" });
        var redo = await new RedoCommand(manager).ExecuteAsync(new[] { "2" });

        Assert.False(undo.Success);
        Assert.Equal(UndoCommand.Usage, undo.Message);
        Assert.False(redo.Success);
        Assert.Equal(RedoCommand.Usage, redo.Message);
    }

    [Fact]
    public async Task Undo_InNonGitWorkspace_ReportsTheLimitation()
    {
        using var workspace = TempGitWorkspace.CreatePlainDirectory();
        using var manager = UndoManager.Create(workspace.WorkspacePath, "s", workspace.SnapshotRoot);

        var result = await new UndoCommand(manager).ExecuteAsync(Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Contains("Git repository", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Undo_RestoresFilesAndPutsThePromptBackInTheComposer()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        using var manager = UndoManager.Create(workspace.WorkspacePath, "s", workspace.SnapshotRoot);
        var turn = manager.BeginTurn("rewrite a.txt");
        workspace.WriteFile("a.txt", "two\n");
        manager.CompleteTurn(turn);

        string? composer = null;
        var result = await new UndoCommand(manager, text => composer = text)
            .ExecuteAsync(Array.Empty<string>());

        Assert.True(result.Success, result.Message);
        Assert.Equal("rewrite a.txt", composer);
        Assert.Equal("one\n", workspace.ReadFile("a.txt"));

        var redoResult = await new RedoCommand(manager).ExecuteAsync(Array.Empty<string>());
        Assert.True(redoResult.Success, redoResult.Message);
        Assert.Equal("two\n", workspace.ReadFile("a.txt"));
    }

    [Fact]
    public async Task Undo_StillSucceedsWhenTheComposerCallbackThrows()
    {
        using var workspace = TempGitWorkspace.CreateGitRepository();
        workspace.WriteFile("a.txt", "one\n");
        workspace.CommitAll("init");

        using var manager = UndoManager.Create(workspace.WorkspacePath, "s", workspace.SnapshotRoot);
        var turn = manager.BeginTurn("rewrite a.txt");
        workspace.WriteFile("a.txt", "two\n");
        manager.CompleteTurn(turn);

        var result = await new UndoCommand(manager, _ => throw new InvalidOperationException("no composer"))
            .ExecuteAsync(Array.Empty<string>());

        Assert.True(result.Success, result.Message);
        Assert.Equal("one\n", workspace.ReadFile("a.txt"));
    }

    [Fact]
    public void InlineHelp_ListsUndoAndRedo()
    {
        var commands = SlashCommandCatalog.CreateInlineHelpCommands();

        Assert.Single(commands, c => c.Name == "undo");
        Assert.Single(commands, c => c.Name == "redo");
        var names = commands.Select(c => c.Name).Concat(commands.SelectMany(c => c.Aliases)).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void InteractiveHelp_DocumentsUndoAndRedo()
    {
        var help = HelpText.InteractiveHelpMarkdown();

        Assert.Contains("/undo", help, StringComparison.Ordinal);
        Assert.Contains("/redo", help, StringComparison.Ordinal);
    }
}
