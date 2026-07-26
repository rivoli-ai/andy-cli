using System;
using System.IO;
using System.Linq;
using Andy.Cli.Commands;
using Andy.Cli.Commands.Custom;
using Xunit;

namespace Andy.Cli.Tests.Commands.Custom;

public class CustomCommandDiscoveryTests : IDisposable
{
    private readonly CustomCommandTestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void NoCommandDirectories_YieldsEmptyCatalogAndNoDiagnostics()
    {
        var empty = Path.Combine(_ws.Root, "nothing-here");
        Directory.CreateDirectory(empty);

        var result = CustomCommandDiscovery.Discover(empty, empty);

        Assert.Empty(result.Commands);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void UserAndProjectCommands_BothLoad_WithSourceRecorded()
    {
        _ws.WriteUser("personal.md", "---\ndescription: Personal helper\n---\nDo the personal thing.");
        _ws.WriteProject("review.md", "---\ndescription: Review the diff\n---\nReview the current diff.");

        var result = _ws.Discover();

        Assert.Equal(new[] { "personal", "review" }, result.Commands.Select(c => c.Name).ToArray());
        Assert.Equal(CustomCommandSource.User, result.Commands[0].Source);
        Assert.Equal(CustomCommandSource.Project, result.Commands[1].Source);
        Assert.Equal("Review the diff", result.Commands[1].Description);
    }

    [Fact]
    public void ProjectCommand_WinsOverUserCommandOfTheSameName()
    {
        _ws.WriteUser("review.md", "---\ndescription: User review\n---\nUser body.");
        var projectPath = _ws.WriteProject("review.md", "---\ndescription: Project review\n---\nProject body.");

        var result = _ws.Discover();

        var review = Assert.Single(result.Commands);
        Assert.Equal(CustomCommandSource.Project, review.Source);
        Assert.Equal("Project body.", review.Template);
        Assert.Equal(projectPath, review.FilePath);
        Assert.Single(review.ShadowedFilePaths);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("shadowed by"));
    }

    [Fact]
    public void Precedence_IsIndependentOfWhichFileWasWrittenFirst()
    {
        // The user file is created after the project file here; the result must not change.
        _ws.WriteProject("review.md", "Project body.");
        _ws.WriteUser("review.md", "User body.");

        var result = _ws.Discover();

        Assert.Equal(CustomCommandSource.Project, Assert.Single(result.Commands).Source);
    }

    [Fact]
    public void Ordering_IsStableAcrossRepeatedScans()
    {
        _ws.WriteProject("zeta.md", "Z");
        _ws.WriteProject("alpha.md", "A");
        _ws.WriteUser("mid.md", "M");
        _ws.WriteProject("git/commit.md", "C");

        var first = _ws.Discover().Commands.Select(c => c.Name).ToArray();
        var second = _ws.Discover().Commands.Select(c => c.Name).ToArray();

        Assert.Equal(new[] { "alpha", "git:commit", "mid", "zeta" }, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void NestedFilenames_BecomeColonSeparatedNames()
    {
        _ws.WriteProject("git/pr/describe.md", "Describe the PR.");

        var command = Assert.Single(_ws.Discover().Commands);

        Assert.Equal("git:pr:describe", command.Name);
        Assert.Equal("git/pr/describe", command.SlashPathForm);
    }

    [Fact]
    public void NestedCommand_IsFindableByBothColonAndPathForm()
    {
        _ws.WriteProject("git/commit.md", "Write a commit message.");
        var catalog = _ws.Catalog();

        Assert.NotNull(catalog.Find("git:commit"));
        Assert.NotNull(catalog.Find("git/commit"));
        Assert.NotNull(catalog.Find("/GIT:COMMIT"));
        Assert.Null(catalog.Find("git"));
    }

    [Fact]
    public void FileNamesWithSpaces_AreRejectedWithADiagnostic_NotACrash()
    {
        _ws.WriteProject("my command.md", "Body.");
        _ws.WriteProject("ok.md", "Body.");

        var result = _ws.Discover();

        Assert.Equal(new[] { "ok" }, result.Commands.Select(c => c.Name).ToArray());
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == CustomCommandDiagnosticSeverity.Error && d.Message.Contains("Spaces are not allowed"));
    }

    [Fact]
    public void BuiltInNames_CannotBeShadowedByAMarkdownFile()
    {
        foreach (var reserved in new[] { "help", "exit", "permissions", "model", "clear", "commands" })
            _ws.WriteProject(reserved + ".md", "Hijacked.");
        // Aliases are reserved too, so /m and /perms cannot be repointed either.
        _ws.WriteProject("m.md", "Hijacked alias.");
        _ws.WriteProject("perms.md", "Hijacked alias.");
        _ws.WriteProject("release.md", "A genuinely new command.");

        var result = _ws.Discover();

        Assert.Equal(new[] { "release" }, result.Commands.Select(c => c.Name).ToArray());
        Assert.Equal(8, result.Diagnostics.Count(d => d.Message.Contains("built-in command")));
    }

    [Fact]
    public void ReservedNames_CoverEveryBuiltInNameAndAlias()
    {
        foreach (var builtIn in SlashCommandCatalog.CreateInlineHelpCommands())
        {
            Assert.Contains(builtIn.Name, SlashCommandCatalog.ReservedCommandNames);
            foreach (var alias in builtIn.Aliases)
                Assert.Contains(alias, SlashCommandCatalog.ReservedCommandNames);
        }
    }

    [Fact]
    public void DuplicateNamesWithinOneRoot_ResolveDeterministically()
    {
        // "nested/dup.md" and "nested.dup.md" both normalize to "nested:dup"? No - the second
        // is a single segment "nested.dup". Use two real duplicates instead: same relative name
        // reached through different case.
        _ws.WriteProject("Dup.md", "Upper body.");
        _ws.WriteProject("dup.md", "Lower body.");

        var result = _ws.Discover();

        // On a case-insensitive filesystem only one file exists; on a case-sensitive one both
        // exist and ordinal path order decides. Either way exactly one command survives and the
        // result is identical on a rescan.
        var command = Assert.Single(result.Commands);
        Assert.Equal("dup", command.Name);
        Assert.Equal(command.FilePath, Assert.Single(_ws.Discover().Commands).FilePath);
    }

    [Fact]
    public void InvalidFrontmatter_IsReportedWithoutLosingTheCommand()
    {
        _ws.WriteProject("broken.md", "---\ndescription Review\nthis is not yaml\n---\nBody text.");

        var result = _ws.Discover();

        var command = Assert.Single(result.Commands);
        Assert.Equal("Body text.", command.Template);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Message.Contains("expected 'key: value'")));
    }

    [Fact]
    public void UnclosedFrontmatter_IsAWarningAndTheWholeFileBecomesTheTemplate()
    {
        _ws.WriteProject("unclosed.md", "---\ndescription: Nope\nBody text.");

        var result = _ws.Discover();

        Assert.Single(result.Commands);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("never closed"));
    }

    [Fact]
    public void UnknownFrontmatterFields_AreReportedAndIgnored()
    {
        _ws.WriteProject("odd.md", "---\ndescription: Fine\nfavourite-colour: blue\n---\nBody.");

        var result = _ws.Discover();

        Assert.Equal("Fine", Assert.Single(result.Commands).Description);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown frontmatter field 'favourite-colour'"));
    }

    [Theory]
    [InlineData("allowed-tools")]
    [InlineData("permissions")]
    [InlineData("agent")]
    [InlineData("bash")]
    [InlineData("shell")]
    public void SecuritySensitiveFrontmatterFields_AreRefused(string field)
    {
        _ws.WriteProject("escalate.md", $"---\ndescription: Fine\n{field}: everything\n---\nBody.");

        var result = _ws.Discover();

        var command = Assert.Single(result.Commands);
        Assert.Null(command.Mode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == CustomCommandDiagnosticSeverity.Error &&
            d.Message.Contains("cannot grant permissions"));
    }

    [Fact]
    public void ProviderModelAndMode_AreParsedAsAdvisoryMetadata()
    {
        _ws.WriteProject("tuned.md", "---\ndescription: Tuned\nprovider: openai\nmodel: \"gpt-5\"\nmode: 'plan'\n---\nBody.");

        var command = Assert.Single(_ws.Discover().Commands);

        Assert.Equal("openai", command.Provider);
        Assert.Equal("gpt-5", command.Model);
        Assert.Equal("plan", command.Mode);
    }

    [Fact]
    public void MissingDescription_FallsBackToTheFirstBodyLine()
    {
        _ws.WriteProject("nodesc.md", "# Prepare the release\n\nDo all the things.");

        var command = Assert.Single(_ws.Discover().Commands);

        Assert.Equal("Prepare the release", command.Description);
    }

    [Fact]
    public void EmptyTemplate_IsRejected()
    {
        _ws.WriteProject("blank.md", "---\ndescription: Nothing\n---\n\n\n");

        var result = _ws.Discover();

        Assert.Empty(result.Commands);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("empty prompt template"));
    }

    [Fact]
    public void OversizedTemplate_IsRejectedBeforeItCanReachAPrompt()
    {
        var limits = new CustomCommandLimits { MaxTemplateBytes = 128 };
        _ws.WriteProject("huge.md", new string('x', 500));
        _ws.WriteProject("small.md", "fine");

        var result = _ws.Discover(limits);

        Assert.Equal(new[] { "small" }, result.Commands.Select(c => c.Name).ToArray());
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == CustomCommandDiagnosticSeverity.Error && d.Message.Contains("over the 128-byte limit"));
    }

    [Fact]
    public void DirectoryDepth_IsBounded()
    {
        var limits = new CustomCommandLimits { MaxDirectoryDepth = 1 };
        _ws.WriteProject("a/b/c/deep.md", "Body.");
        _ws.WriteProject("a/shallow.md", "Body.");

        var result = _ws.Discover(limits);

        Assert.Equal(new[] { "a:shallow" }, result.Commands.Select(c => c.Name).ToArray());
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("directory levels"));
    }

    [Fact]
    public void NonMarkdownFiles_AreIgnored()
    {
        _ws.WriteProject("notes.txt", "not a command");
        _ws.WriteProject("real.md", "a command");

        Assert.Equal(new[] { "real" }, _ws.Discover().Commands.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Roots_AreUserFirstThenProject()
    {
        var roots = CustomCommandDiscovery.DefaultRoots(_ws.Workspace, _ws.Home);

        Assert.Equal(2, roots.Count);
        Assert.Equal(_ws.UserCommands, roots[0]);
        Assert.Equal(_ws.ProjectCommands, roots[1]);
    }
}
