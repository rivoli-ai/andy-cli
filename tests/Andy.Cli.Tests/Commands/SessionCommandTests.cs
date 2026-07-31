using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Services.Sessions;
using Andy.Cli.Tests.Services.Sessions;
using Xunit;

namespace Andy.Cli.Tests.Commands;

/// <summary>
/// The /session command surface (issue #285): export, import, fork, rename, and stats,
/// shared by the interactive slash command and the one-shot CLI command.
/// </summary>
public class SessionCommandTests : IDisposable
{
    private readonly string _storeDirectory;
    private readonly string _workDirectory;
    private readonly SessionStore _store;

    public SessionCommandTests()
    {
        _storeDirectory = SessionArchiveTestData.NewTempDirectory("session-command-store");
        _workDirectory = SessionArchiveTestData.NewTempDirectory("session-command-work");
        Directory.CreateDirectory(_storeDirectory);
        Directory.CreateDirectory(_workDirectory);
        _store = SessionArchiveTestData.CreateStore(_storeDirectory);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _storeDirectory, _workDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
        GC.SuppressFinalize(this);
    }

    private string Save(int turns = 3, string? title = null, SessionUsage? usage = null)
    {
        var id = SessionStore.NewSessionId();
        _store.Save(id, SessionArchiveTestData.Snapshot(turns), "openai", "gpt-4o",
            new SessionSaveOptions { Title = title, Usage = usage });
        return id;
    }

    private SessionCommand Command(string? currentId = null) =>
        new(_store, currentId is null ? null : () => currentId);

    private string Work(string name) => Path.Combine(_workDirectory, name);

    [Fact]
    public async Task NoArguments_ShowsUsage()
    {
        var result = await Command().ExecuteAsync(Array.Empty<string>());

        Assert.True(result.Success);
        Assert.Contains("session export", result.Message);
        Assert.Contains("session fork", result.Message);
    }

    [Fact]
    public async Task UnknownSubcommand_Fails()
    {
        var result = await Command().ExecuteAsync(new[] { "frobnicate" });

        Assert.False(result.Success);
        Assert.Contains("Unknown subcommand", result.Message);
    }

    [Fact]
    public async Task Export_WritesAnArchiveForTheNamedSession()
    {
        var id = Save();
        var target = Work("out.json");

        var result = await Command().ExecuteAsync(new[] { "export", id, "--out", target });

        Assert.True(result.Success);
        Assert.True(File.Exists(target));
        Assert.Contains("sha256", result.Message);
    }

    [Fact]
    public async Task Export_DefaultsToTheCurrentSession()
    {
        var id = Save();
        var target = Work("current.json");

        var result = await Command(currentId: id).ExecuteAsync(new[] { "export", "--out", target });

        Assert.True(result.Success);
        Assert.Contains(id, result.Message);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Export_WithoutAnIdAndWithoutACurrentSession_Fails()
    {
        var result = await Command().ExecuteAsync(new[] { "export" });

        Assert.False(result.Success);
        Assert.Contains("no current session", result.Message);
    }

    [Fact]
    public async Task Export_RejectsAnUnsafeSessionId()
    {
        var result = await Command().ExecuteAsync(new[] { "export", "../../evil" });

        Assert.False(result.Success);
        Assert.Contains("Invalid session id", result.Message);
    }

    [Fact]
    public async Task ExportMarkdown_WithToolAndMetadataFlags()
    {
        var id = SessionStore.NewSessionId();
        _store.Save(id, SessionArchiveTestData.RichSnapshot(), "openai", "gpt-4o");
        var target = Work("out.md");

        var result = await Command().ExecuteAsync(
            new[] { "export", id, "--out", target, "--markdown", "--tools", "--metadata" });

        Assert.True(result.Success);
        var markdown = File.ReadAllText(target);
        Assert.Contains("## Metadata", markdown);
        Assert.Contains("### Tool call: `read_file`", markdown);
    }

    [Fact]
    public async Task Import_InstallsTheArchiveAndReportsTheNewId()
    {
        var id = Save();
        var archive = Work("a.json");
        await Command().ExecuteAsync(new[] { "export", id, "--out", archive });

        var result = await Command().ExecuteAsync(new[] { "import", archive });

        Assert.True(result.Success);
        Assert.Contains("Imported session", result.Message);
        Assert.Contains("already in use", result.Message);
        Assert.Equal(2, Directory.GetFiles(_storeDirectory).Length);
    }

    [Fact]
    public async Task ImportDryRun_ChangesNothing()
    {
        var id = Save();
        var archive = Work("a.json");
        await Command().ExecuteAsync(new[] { "export", id, "--out", archive });

        var result = await Command().ExecuteAsync(new[] { "import", archive, "--dry-run" });

        Assert.True(result.Success);
        Assert.Contains("Dry run", result.Message);
        Assert.Single(Directory.GetFiles(_storeDirectory));
    }

    [Fact]
    public async Task Import_WithoutAPath_Fails()
    {
        var result = await Command().ExecuteAsync(new[] { "import" });

        Assert.False(result.Success);
        Assert.Contains("Usage: session import", result.Message);
    }

    [Fact]
    public async Task Import_OfACorruptArchive_FailsWithoutThrowing()
    {
        var archive = Work("corrupt.json");
        File.WriteAllText(archive, "{ nope");

        var result = await Command().ExecuteAsync(new[] { "import", archive });

        Assert.False(result.Success);
        Assert.Contains("not valid JSON", result.Message);
        Assert.Empty(Directory.GetFiles(_storeDirectory));
    }

    [Fact]
    public async Task Fork_CreatesANewSessionAtTheGivenBoundary()
    {
        var id = Save(turns: 5);

        var result = await Command().ExecuteAsync(new[] { "fork", id, "--at", "3" });

        Assert.True(result.Success);
        Assert.Contains("before turn 3", result.Message);
        var fork = _store.List().Single(s => s.SessionId != id);
        Assert.Equal(2, fork.TurnCount);
    }

    [Fact]
    public async Task Fork_DefaultsToTheCurrentSessionAndForksItWhole()
    {
        var id = Save(turns: 4);

        var result = await Command(currentId: id).ExecuteAsync(new[] { "fork" });

        Assert.True(result.Success);
        var fork = _store.List().Single(s => s.SessionId != id);
        Assert.Equal(4, fork.TurnCount);
    }

    [Fact]
    public async Task Fork_WithANonNumericBoundary_Fails()
    {
        var id = Save();

        var result = await Command().ExecuteAsync(new[] { "fork", id, "--at", "middle" });

        Assert.False(result.Success);
        Assert.Contains("--at expects a turn number", result.Message);
    }

    [Fact]
    public async Task Fork_AtTurnOne_FailsCleanly()
    {
        var id = Save();

        var result = await Command().ExecuteAsync(new[] { "fork", id, "--at", "1" });

        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(_storeDirectory));
    }

    [Fact]
    public async Task Rename_WithAnExplicitId()
    {
        var id = Save();

        var result = await Command().ExecuteAsync(new[] { "rename", id, "New", "title", "here" });

        Assert.True(result.Success);
        Assert.Equal("New title here", _store.Load(id)!.Summary.Title);
    }

    [Fact]
    public async Task Rename_WithoutAnIdUsesTheCurrentSession()
    {
        var id = Save();

        var result = await Command(currentId: id).ExecuteAsync(new[] { "rename", "Just", "a", "title" });

        Assert.True(result.Success);
        Assert.Equal("Just a title", _store.Load(id)!.Summary.Title);
    }

    [Fact]
    public async Task Rename_MakesTheSessionDiscoverableInTheListing()
    {
        var id = Save();
        await Command().ExecuteAsync(new[] { "rename", id, "Findable", "session" });

        var listing = await Command().ExecuteAsync(new[] { "list" });

        Assert.Contains("Findable session", listing.Message);
    }

    [Fact]
    public async Task Stats_ForOneSession()
    {
        var id = Save(usage: new SessionUsage
        {
            InputTokens = 100,
            OutputTokens = 50,
            ReasoningTokens = 10,
            CacheReadTokens = 80,
            CacheWriteTokens = 5,
            EstimatedCostUsd = 0.001m
        });

        var result = await Command().ExecuteAsync(new[] { "stats", id });

        Assert.True(result.Success);
        Assert.Contains("Reasoning tokens: 10", result.Message);
        Assert.Contains("Cache read:       80", result.Message);
        Assert.Contains("$0.0010", result.Message);
    }

    [Fact]
    public async Task StatsAll_AggregatesEverySession()
    {
        Save(usage: new SessionUsage { InputTokens = 100, OutputTokens = 10, EstimatedCostUsd = 0.001m });
        Save(usage: new SessionUsage { InputTokens = 200, OutputTokens = 20, EstimatedCostUsd = 0.002m });

        var result = await Command().ExecuteAsync(new[] { "stats", "--all" });

        Assert.True(result.Success);
        Assert.Contains("Usage across 2 sessions", result.Message);
        Assert.Contains("300", result.Message);
        Assert.Contains("$0.0030", result.Message);
    }

    [Fact]
    public async Task Stats_ForAMissingSession_Fails()
    {
        var result = await Command().ExecuteAsync(new[] { "stats", SessionStore.NewSessionId() });

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task Stats_WithNoSessionsAtAll_ReportsTheEmptyState()
    {
        var result = await Command().ExecuteAsync(new[] { "stats" });

        Assert.True(result.Success);
        Assert.Equal(SessionsCommand.NoSessionsMessage, result.Message);
    }

    [Fact]
    public async Task List_ReusesTheSessionsListing()
    {
        var id = Save();

        var result = await Command().ExecuteAsync(new[] { "list" });

        Assert.True(result.Success);
        Assert.Contains(id, result.Message);
    }

    [Fact]
    public void ParseOptions_HandlesFlagsValuesAndEqualsForm()
    {
        var parsed = SessionCommand.ParseOptions(new[]
        {
            "abc", "--markdown", "--out", "file.json", "--title=Hello", "--tools"
        });

        Assert.Equal(new[] { "abc" }, parsed.Positional);
        Assert.True(parsed.HasFlag("markdown"));
        Assert.True(parsed.HasFlag("tools"));
        Assert.Equal("file.json", parsed.GetValue("out"));
        Assert.Equal("Hello", parsed.GetValue("title"));
    }

    [Fact]
    public void CommandMetadata_MatchesTheSlashCommandCatalog()
    {
        var command = Command();

        Assert.Equal("session", command.Name);
        Assert.Contains(SlashCommandCatalog.CreateInlineHelpCommands(), c => c.Name == "session");
        Assert.Contains("/session export", HelpText.InteractiveHelpMarkdown());
        Assert.Contains("session ", HelpText.CommandLineHelp());
    }
}
