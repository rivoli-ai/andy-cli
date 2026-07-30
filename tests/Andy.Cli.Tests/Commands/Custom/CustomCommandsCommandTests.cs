using System;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Commands.Custom;
using Xunit;

namespace Andy.Cli.Tests.Commands.Custom;

public class CustomCommandsCommandTests : IDisposable
{
    private readonly CustomCommandTestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private CustomCommandsCommand Build(out CustomCommandCatalog catalog)
    {
        catalog = _ws.Catalog();
        return new CustomCommandsCommand(catalog);
    }

    [Fact]
    public async Task List_WithNoCommands_ShowsTheRootsHonestly()
    {
        var cmd = Build(out _);

        var result = await cmd.ExecuteAsync(Array.Empty<string>());

        Assert.True(result.Success, result.Message);
        Assert.Contains("No Markdown commands found", result.Message);
        Assert.Contains(_ws.UserCommands, result.Message);
        Assert.Contains(_ws.ProjectCommands, result.Message);
    }

    [Fact]
    public async Task List_ShowsEachCommandWithItsSource()
    {
        _ws.WriteUser("personal.md", "---\ndescription: Personal helper\n---\nBody.");
        _ws.WriteProject("review.md", "---\ndescription: Review the diff\n---\nBody.");
        var cmd = Build(out _);

        var result = await cmd.ExecuteAsync(new[] { "list" });

        Assert.Contains("/personal", result.Message);
        Assert.Contains("[user]", result.Message);
        Assert.Contains("/review", result.Message);
        Assert.Contains("[project]", result.Message);
    }

    [Fact]
    public async Task Info_ShowsFileMetadataAndTemplate()
    {
        var path = _ws.WriteProject("git/commit.md",
            "---\ndescription: Draft a commit message\nmodel: gpt-5\n---\nWrite a message for $1.");
        var cmd = Build(out _);

        var result = await cmd.ExecuteAsync(new[] { "info", "git:commit" });

        Assert.True(result.Success, result.Message);
        Assert.Contains("/git:commit", result.Message);
        Assert.Contains(path, result.Message);
        Assert.Contains("gpt-5", result.Message);
        Assert.Contains("advisory metadata", result.Message);
        Assert.Contains("Write a message for $1.", result.Message);
        Assert.Contains("$1", result.Message);
    }

    [Fact]
    public async Task Info_UnknownCommand_Fails()
    {
        var cmd = Build(out _);

        var result = await cmd.ExecuteAsync(new[] { "info", "nope" });

        Assert.False(result.Success);
        Assert.Contains("No Markdown command named", result.Message);
    }

    [Fact]
    public async Task Reload_PicksUpANewFileWithoutRebuildingTheCommand()
    {
        var cmd = Build(out var catalog);
        Assert.Empty(catalog.Commands);

        _ws.WriteProject("fresh.md", "Body.");

        var result = await cmd.ExecuteAsync(new[] { "reload" });

        Assert.Contains("1 command(s)", result.Message);
        Assert.Equal(new[] { "fresh" }, catalog.Commands.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task Diagnostics_ReportsRejectedFiles()
    {
        _ws.WriteProject("help.md", "Hijack the built-in.");
        var cmd = Build(out _);

        var result = await cmd.ExecuteAsync(new[] { "diagnostics" });

        Assert.Contains("built-in command", result.Message);
    }

    [Fact]
    public async Task Help_DocumentsTheSecurityBoundary()
    {
        var cmd = Build(out _);

        var result = await cmd.ExecuteAsync(new[] { "help" });

        Assert.Contains("cannot run a shell", result.Message);
        Assert.Contains("bypass plan mode", result.Message);
        Assert.Contains("$ARGUMENTS", result.Message);
    }

    [Fact]
    public async Task UnknownSubcommand_Fails()
    {
        var cmd = Build(out _);

        var result = await cmd.ExecuteAsync(new[] { "frobnicate" });

        Assert.False(result.Success);
        Assert.Contains("Unknown subcommand", result.Message);
    }

    [Fact]
    public void CommandMetadata_MatchesTheSlashCatalogEntry()
    {
        var cmd = Build(out _);
        var entry = Assert.Single(SlashCommandCatalog.CreateInlineHelpCommands(), c => c.Name == "commands");

        Assert.Equal(entry.Name, cmd.Name);
        Assert.Equal(entry.Description, cmd.Description);
        Assert.Equal(entry.Aliases, cmd.Aliases);
    }
}

public class SlashCommandCatalogCustomCommandTests : IDisposable
{
    private readonly CustomCommandTestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void InlineHelp_WithoutCustomCommands_MatchesTheBuiltInList()
    {
        var withNull = SlashCommandCatalog.CreateInlineHelpCommands(null);

        Assert.Equal(
            SlashCommandCatalog.CreateInlineHelpCommands().Select(c => c.Name),
            withNull.Select(c => c.Name));
    }

    [Fact]
    public void InlineHelp_AppendsCustomCommandsAfterBuiltIns_WithTheirSource()
    {
        _ws.WriteProject("release.md", "---\ndescription: Prepare a release\n---\nBody.");
        _ws.WriteUser("notes.md", "---\ndescription: Personal notes\n---\nBody.");

        var entries = SlashCommandCatalog.CreateInlineHelpCommands(_ws.Catalog().Commands);
        var builtInCount = SlashCommandCatalog.CreateInlineHelpCommands().Length;

        Assert.Equal(builtInCount + 2, entries.Length);
        Assert.Equal("notes", entries[builtInCount].Name);
        Assert.Equal("[user] Personal notes", entries[builtInCount].Description);
        Assert.Equal("release", entries[builtInCount + 1].Name);
        Assert.Equal("[project] Prepare a release", entries[builtInCount + 1].Description);
    }

    [Fact]
    public void InlineHelp_NestedCommandsExposeThePathFormAsAnAlias()
    {
        _ws.WriteProject("git/commit.md", "Body.");

        var entry = Assert.Single(
            SlashCommandCatalog.CreateInlineHelpCommands(_ws.Catalog().Commands),
            c => c.Name == "git:commit");

        Assert.Equal(new[] { "git/commit" }, entry.Aliases);
    }

    [Fact]
    public void InlineHelp_NeverLetsACustomCommandDisplaceABuiltIn()
    {
        // Belt and braces: even if a definition with a reserved name reached this method
        // (discovery already rejects them), the built-in entry must remain the only one.
        var smuggled = new[]
        {
            new CustomCommandDefinition("help", "Not the real help", "Body.", "/tmp/help.md", CustomCommandSource.Project)
        };

        var entries = SlashCommandCatalog.CreateInlineHelpCommands(smuggled);

        var help = Assert.Single(entries, c => c.Name == "help");
        Assert.Equal("Show help information", help.Description);
    }
}
