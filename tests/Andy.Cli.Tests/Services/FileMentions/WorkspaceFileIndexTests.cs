using System;
using System.Linq;
using Andy.Cli.Services.FileMentions;
using Xunit;

namespace Andy.Cli.Tests.Services.FileMentions;

public class WorkspaceFileIndexTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private WorkspaceFileIndex Index() => new(_workspace.Root, new WorkspaceIgnoreRules(_workspace.Root));

    [Fact]
    public void GetEntries_ListsFilesAndDirectoriesRelativeToRoot()
    {
        _workspace.WriteFile("src/Foo.cs", "x");
        _workspace.WriteFile("README.md", "y");

        var entries = Index().GetEntries();

        Assert.Contains(entries, e => e.RelativePath == "src/Foo.cs" && !e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "src" && e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "README.md" && !e.IsDirectory);
    }

    [Fact]
    public void GetEntries_SkipsDefaultIgnoredDirectories()
    {
        _workspace.WriteFile("node_modules/pkg/index.js", "x");
        _workspace.WriteFile("obj/Debug/generated.cs", "x");
        _workspace.WriteFile(".git/config", "x");
        _workspace.WriteFile("src/Keep.cs", "x");

        var entries = Index().GetEntries();

        Assert.DoesNotContain(entries, e => e.RelativePath.StartsWith("node_modules", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.RelativePath.StartsWith("obj", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.RelativePath.StartsWith(".git", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.RelativePath == "src/Keep.cs");
    }

    [Fact]
    public void GetEntries_HonoursRootGitignore()
    {
        _workspace.WriteFile(".gitignore", "*.log\nsecrets/\n");
        _workspace.WriteFile("app.log", "x");
        _workspace.WriteFile("secrets/key.txt", "x");
        _workspace.WriteFile("app.txt", "x");

        var entries = Index().GetEntries();

        Assert.DoesNotContain(entries, e => e.RelativePath == "app.log");
        Assert.DoesNotContain(entries, e => e.RelativePath.StartsWith("secrets", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.RelativePath == "app.txt");
    }

    [Fact]
    public void GetEntries_HonoursNestedGitignore()
    {
        _workspace.WriteFile("pkg/.gitignore", "generated.txt\n");
        _workspace.WriteFile("pkg/generated.txt", "x");
        _workspace.WriteFile("pkg/source.txt", "x");
        _workspace.WriteFile("generated.txt", "x");

        var entries = Index().GetEntries();

        Assert.DoesNotContain(entries, e => e.RelativePath == "pkg/generated.txt");
        Assert.Contains(entries, e => e.RelativePath == "pkg/source.txt");
        Assert.Contains(entries, e => e.RelativePath == "generated.txt");
    }

    [Fact]
    public void GetEntries_HonoursNegationRules()
    {
        _workspace.WriteFile(".gitignore", "*.log\n!keep.log\n");
        _workspace.WriteFile("drop.log", "x");
        _workspace.WriteFile("keep.log", "x");

        var entries = Index().GetEntries();

        Assert.DoesNotContain(entries, e => e.RelativePath == "drop.log");
        Assert.Contains(entries, e => e.RelativePath == "keep.log");
    }

    [Fact]
    public void GetEntries_HonoursAnchoredAndDoubleStarRules()
    {
        _workspace.WriteFile(".gitignore", "/top.txt\ndocs/**/draft.md\n");
        _workspace.WriteFile("top.txt", "x");
        _workspace.WriteFile("nested/top.txt", "x");
        _workspace.WriteFile("docs/a/b/draft.md", "x");
        _workspace.WriteFile("docs/a/final.md", "x");

        var entries = Index().GetEntries();

        Assert.DoesNotContain(entries, e => e.RelativePath == "top.txt");
        Assert.Contains(entries, e => e.RelativePath == "nested/top.txt");
        Assert.DoesNotContain(entries, e => e.RelativePath == "docs/a/b/draft.md");
        Assert.Contains(entries, e => e.RelativePath == "docs/a/final.md");
    }

    [Fact]
    public void GetEntries_IgnoresCommentsAndBlankLines()
    {
        _workspace.WriteFile(".gitignore", "# a comment\n\n   \n*.tmp\n");
        _workspace.WriteFile("scratch.tmp", "x");
        _workspace.WriteFile("scratch.txt", "x");

        var entries = Index().GetEntries();

        Assert.DoesNotContain(entries, e => e.RelativePath == "scratch.tmp");
        Assert.Contains(entries, e => e.RelativePath == "scratch.txt");
    }

    [Fact]
    public void GetEntries_RespectsTheEntryCap()
    {
        for (int i = 0; i < 30; i++)
        {
            _workspace.WriteFile($"f{i}.txt", "x");
        }

        var index = new WorkspaceFileIndex(_workspace.Root, new WorkspaceIgnoreRules(_workspace.Root), maxEntries: 10);
        var entries = index.GetEntries();

        Assert.Equal(10, entries.Count);
        Assert.True(index.WasTruncated);
    }

    [Fact]
    public void Invalidate_PicksUpNewFiles()
    {
        _workspace.WriteFile("one.txt", "x");
        var index = Index();
        Assert.Single(index.GetEntries());

        _workspace.WriteFile("two.txt", "x");
        index.Invalidate();

        Assert.Equal(2, index.GetEntries().Count);
    }

    [Fact]
    public void IsIgnored_UsedDirectlyForPointQueries()
    {
        _workspace.WriteFile(".gitignore", "build/\n*.key\n");
        var rules = new WorkspaceIgnoreRules(_workspace.Root);

        Assert.True(rules.IsIgnored("build", isDirectory: true));
        Assert.True(rules.IsIgnored("build/output.txt", isDirectory: false));
        Assert.True(rules.IsIgnored("certs/server.key", isDirectory: false));
        Assert.False(rules.IsIgnored("src/Foo.cs", isDirectory: false));
    }
}
