using System;
using System.Linq;
using Andy.Cli.Services.FileMentions;
using Xunit;

namespace Andy.Cli.Tests.Services.FileMentions;

public class FileMentionSearchServiceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private FileMentionSearchService Search(FrecencyStore? frecency = null) =>
        new(new WorkspaceFileIndex(_workspace.Root, new WorkspaceIgnoreRules(_workspace.Root)), frecency);

    [Fact]
    public void Search_EmptyQuery_ReturnsShallowEntriesFirst()
    {
        _workspace.WriteFile("README.md", "x");
        _workspace.WriteFile("a/b/c/deep.md", "x");

        var results = Search().Search(string.Empty, limit: 20);

        int readmeIndex = results.ToList().FindIndex(r => r.RelativePath == "README.md");
        int deepIndex = results.ToList().FindIndex(r => r.RelativePath == "a/b/c/deep.md");
        Assert.True(readmeIndex >= 0);
        Assert.True(deepIndex < 0 || readmeIndex < deepIndex);
    }

    [Fact]
    public void Search_MatchesSubsequencesNotJustPrefixes()
    {
        _workspace.WriteFile("src/Andy.Cli/Program.cs", "x");
        _workspace.WriteFile("src/Other/Unrelated.cs", "x");

        var results = Search().Search("prog");

        Assert.Equal("src/Andy.Cli/Program.cs", results[0].RelativePath);
    }

    [Fact]
    public void Search_PrefersFileNameMatchesOverScatteredPathMatches()
    {
        _workspace.WriteFile("feed/view/other.cs", "x");
        _workspace.WriteFile("widgets/FeedView.cs", "x");

        var results = Search().Search("feedview");

        Assert.Equal("widgets/FeedView.cs", results[0].RelativePath);
    }

    [Fact]
    public void Search_NonMatchingQuery_ReturnsNothing()
    {
        _workspace.WriteFile("src/Foo.cs", "x");

        Assert.Empty(Search().Search("zzzzzz"));
    }

    [Fact]
    public void Search_QueryWithDirectorySegment_MatchesFullPath()
    {
        _workspace.WriteFile("src/Foo.cs", "x");
        _workspace.WriteFile("tests/Foo.cs", "x");

        var results = Search().Search("tests/foo");

        Assert.Equal("tests/Foo.cs", results[0].RelativePath);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        _workspace.WriteFile("src/ProgramRunner.cs", "x");

        Assert.Contains(Search().Search("PROGRAMRUNNER"), r => r.RelativePath == "src/ProgramRunner.cs");
    }

    [Fact]
    public void Search_RanksRecentSelectionsAheadOfEquallyGoodMatches()
    {
        _workspace.WriteFile("alpha/Item.cs", "x");
        _workspace.WriteFile("bravo/Item.cs", "x");

        var frecency = new FrecencyStore();
        var search = Search(frecency);

        var before = search.Search("item.cs");
        Assert.Equal(2, before.Count);
        string runnerUp = before[1].RelativePath;

        search.RecordSelection(runnerUp);
        var after = search.Search("item.cs");

        Assert.Equal(runnerUp, after[0].RelativePath);
    }

    [Fact]
    public void Search_ExcludesIgnoredFiles()
    {
        _workspace.WriteFile(".gitignore", "*.env\n");
        _workspace.WriteFile("secrets.env", "x");
        _workspace.WriteFile("settings.json", "x");

        var results = Search().Search("s");

        Assert.DoesNotContain(results, r => r.RelativePath == "secrets.env");
    }

    [Fact]
    public void Search_ObservesTheLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            _workspace.WriteFile($"item{i}.txt", "x");
        }

        Assert.Equal(3, Search().Search("item", limit: 3).Count);
    }

    [Fact]
    public void Suggestion_ExposesDisplayNameDirectoryAndMentionText()
    {
        _workspace.WriteFile("docs/my notes.md", "x");

        var suggestion = Search().Search("my notes").Single(s => !s.IsDirectory);

        Assert.Equal("my notes.md", suggestion.DisplayName);
        Assert.Equal("docs", suggestion.DirectoryName);
        Assert.Equal("@\"docs/my notes.md\"", suggestion.MentionText);
    }

    [Fact]
    public void Suggestion_ForDirectory_RendersTrailingSlash()
    {
        _workspace.CreateDirectory("widgets");

        var suggestion = Search().Search("widgets").Single(s => s.IsDirectory);

        Assert.Equal("widgets/", suggestion.DisplayName);
        Assert.Equal("@widgets/", suggestion.MentionText);
    }

    [Fact]
    public void FrecencyStore_BonusGrowsWithUseAndDecaysWithRecency()
    {
        var store = new FrecencyStore();
        store.Record("a.txt");
        store.Record("b.txt");

        Assert.True(store.GetBonus("b.txt") > store.GetBonus("a.txt"));
        Assert.Equal(0, store.GetBonus("never-picked.txt"));

        int before = store.GetBonus("a.txt");
        store.Record("a.txt");
        Assert.True(store.GetBonus("a.txt") > before);
    }
}
