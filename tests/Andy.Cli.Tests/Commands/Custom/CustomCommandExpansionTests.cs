using System;
using System.IO;
using System.Linq;
using Andy.Cli.Commands.Custom;
using Xunit;

namespace Andy.Cli.Tests.Commands.Custom;

public class CustomCommandArgumentParsingTests
{
    [Fact]
    public void PlainWords_SplitOnWhitespace()
    {
        Assert.Equal(new[] { "one", "two", "three" }, CustomCommandArguments.Parse("one   two\tthree").ToArray());
    }

    [Fact]
    public void DoubleQuotes_GroupWhitespaceAndAreRemoved()
    {
        Assert.Equal(new[] { "hello world", "next" }, CustomCommandArguments.Parse("\"hello world\" next").ToArray());
    }

    [Fact]
    public void SingleQuotes_GroupWhitespaceAndAreRemoved()
    {
        Assert.Equal(new[] { "hello world" }, CustomCommandArguments.Parse("'hello world'").ToArray());
    }

    [Fact]
    public void EscapedQuoteInsideDoubleQuotes_IsLiteral()
    {
        Assert.Equal(new[] { "say \"hi\"" }, CustomCommandArguments.Parse("\"say \\\"hi\\\"\"").ToArray());
    }

    [Fact]
    public void BackslashInsideSingleQuotes_StaysLiteral()
    {
        Assert.Equal(new[] { @"C:\path" }, CustomCommandArguments.Parse(@"'C:\path'").ToArray());
    }

    [Fact]
    public void QuotesTouchingText_JoinIntoOneArgument()
    {
        Assert.Equal(new[] { "prefix-hello world" }, CustomCommandArguments.Parse("prefix-\"hello world\"").ToArray());
    }

    [Fact]
    public void UnterminatedQuote_TakesTheRestOfTheLine_InsteadOfFailing()
    {
        Assert.Equal(new[] { "hello world and more" }, CustomCommandArguments.Parse("\"hello world and more").ToArray());
    }

    [Fact]
    public void EmptyQuotedArgument_IsPreserved()
    {
        Assert.Equal(new[] { "a", "", "b" }, CustomCommandArguments.Parse("a \"\" b").ToArray());
    }

    [Fact]
    public void EmptyInput_YieldsNoArguments()
    {
        Assert.Empty(CustomCommandArguments.Parse(""));
        Assert.Empty(CustomCommandArguments.Parse(null));
        Assert.Empty(CustomCommandArguments.Parse("   "));
    }
}

public class CustomCommandExpanderTests
{
    [Fact]
    public void Arguments_ExpandsToTheRawTextAsTyped()
    {
        var result = CustomCommandExpander.ExpandTemplate("Review: $ARGUMENTS", "\"src/a.cs\" and src/b.cs");

        Assert.Equal("Review: \"src/a.cs\" and src/b.cs", result);
    }

    [Fact]
    public void Positionals_UseUnquotedValues()
    {
        var result = CustomCommandExpander.ExpandTemplate("[$1] [$2]", "\"hello world\" second");

        Assert.Equal("[hello world] [second]", result);
    }

    [Fact]
    public void BracedForms_AreSupportedForBothKinds()
    {
        var result = CustomCommandExpander.ExpandTemplate("${1}nd ${ARGUMENTS}", "a b");

        Assert.Equal("and a b", result);
    }

    [Fact]
    public void MissingPositionals_ExpandToNothing()
    {
        var result = CustomCommandExpander.ExpandTemplate("first=$1 second=$2 third=$3", "only");

        Assert.Equal("first=only second= third=", result);
    }

    [Fact]
    public void NoArgumentsAtAll_LeavesAnEmptyExpansion()
    {
        Assert.Equal("Review: ", CustomCommandExpander.ExpandTemplate("Review: $ARGUMENTS", null));
        Assert.Equal("Review: ", CustomCommandExpander.ExpandTemplate("Review: $ARGUMENTS", "   "));
    }

    [Fact]
    public void DoubleDollar_ProducesALiteralDollarSign()
    {
        Assert.Equal("$ARGUMENTS stays literal", CustomCommandExpander.ExpandTemplate("$$ARGUMENTS stays literal", "x"));
        Assert.Equal("$1", CustomCommandExpander.ExpandTemplate("$$1", "x"));
        Assert.Equal("$", CustomCommandExpander.ExpandTemplate("$$", ""));
    }

    [Fact]
    public void UnrecognisedDollarSequences_StayLiteral()
    {
        // $PATH is not a placeholder, $0 is out of range, $10 is multi-digit, and a bare
        // trailing $ is just a dollar sign.
        Assert.Equal("uses $PATH and $0 and $10 and a trailing $",
            CustomCommandExpander.ExpandTemplate("uses $PATH and $0 and $10 and a trailing $", "arg"));
    }

    [Fact]
    public void DollarFollowedByADigit_IsAlwaysAPlaceholder_SoPricesNeedDoubleDollar()
    {
        // Documented footgun: "$5.00" reads as placeholder $5 followed by ".00".
        Assert.Equal(".00", CustomCommandExpander.ExpandTemplate("$5.00", "one two"));
        Assert.Equal("$5.00", CustomCommandExpander.ExpandTemplate("$$5.00", "one two"));
    }

    [Fact]
    public void SubstitutedText_IsNotRescannedForPlaceholders()
    {
        // A user cannot smuggle a placeholder in through an argument value.
        var result = CustomCommandExpander.ExpandTemplate("$1", "\"$ARGUMENTS\"");

        Assert.Equal("$ARGUMENTS", result);
    }

    [Fact]
    public void TemplateIntrospection_ReportsPlaceholderUsage()
    {
        Assert.True(CustomCommandExpander.ReferencesArguments("Do $ARGUMENTS now"));
        Assert.False(CustomCommandExpander.ReferencesArguments("Do $$ARGUMENTS now"));
        Assert.Equal(3, CustomCommandExpander.MaxPositionalReferenced("$1 $3 $2"));
        Assert.Equal(0, CustomCommandExpander.MaxPositionalReferenced("$$1 no placeholders"));
    }
}

public class CustomCommandCatalogExpansionTests : IDisposable
{
    private readonly CustomCommandTestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void TryExpand_UnknownCommand_Fails()
    {
        var catalog = _ws.Catalog();

        Assert.False(catalog.TryExpand("nope", "", out var prompt, out var error));
        Assert.Null(prompt);
        Assert.Contains("Unknown command", error);
    }

    [Fact]
    public void TryExpand_SubstitutesArgumentsAndKeepsSourceAttribution()
    {
        var path = _ws.WriteProject("review.md", "---\ndescription: Review\n---\nReview $1 for $2.");
        var catalog = _ws.Catalog();

        Assert.True(catalog.TryExpand("review", "\"src/a b.cs\" style", out var prompt, out var error));
        Assert.Null(error);
        Assert.Equal("Review src/a b.cs for style.", prompt!.Text);
        Assert.Equal($"project command /review ({path})", prompt.SourceAttribution);
        Assert.Contains($"(Source: project command /review ({path}))", prompt.ToPromptText());
    }

    [Fact]
    public void MissingArguments_ProduceAWarningNotAFailure()
    {
        _ws.WriteProject("review.md", "Review $1 and $2.");
        var catalog = _ws.Catalog();

        Assert.True(catalog.TryExpand("review", "only", out var prompt, out _));
        Assert.Equal("Review only and .", prompt!.Text);
        Assert.Contains(prompt.Diagnostics, d => d.Message.Contains("only 1 argument(s) were given"));
    }

    [Fact]
    public void FileMentions_BecomeStructuredPartsRetainedOnTheResult()
    {
        _ws.WriteWorkspaceFile("src/target.cs", "class Target { }");
        _ws.WriteProject("explain.md", "Explain @src/target.cs please.");
        var catalog = _ws.Catalog();

        Assert.True(catalog.TryExpand("explain", "", out var prompt, out _));

        var part = Assert.Single(prompt!.Files);
        Assert.Equal("@src/target.cs", part.Mention);
        Assert.Equal("class Target { }", part.Content);
        Assert.False(part.Truncated);
        // The mention stays in the prose; the content rides along as a separate part.
        Assert.Contains("Explain @src/target.cs please.", prompt.Text);
        Assert.Contains("class Target { }", prompt.ToPromptText());
    }

    [Fact]
    public void FileMentions_CanComeFromAnArgument()
    {
        _ws.WriteWorkspaceFile("notes.md", "the notes");
        _ws.WriteProject("read.md", "Read $1 carefully.");
        var catalog = _ws.Catalog();

        Assert.True(catalog.TryExpand("read", "@notes.md", out var prompt, out _));

        Assert.Equal("@notes.md", Assert.Single(prompt!.Files).Mention);
    }

    [Fact]
    public void FileMentions_OutsideTheWorkspace_AreRefused()
    {
        _ws.WriteProject("escape.md", "Show @../home/.andy/commands/escape.md now.");
        var catalog = _ws.Catalog();

        Assert.True(catalog.TryExpand("escape", "", out var prompt, out _));

        Assert.Empty(prompt!.Files);
        Assert.Contains(prompt.Diagnostics, d =>
            d.Severity == CustomCommandDiagnosticSeverity.Error && d.Message.Contains("inside the workspace"));
    }

    [Fact]
    public void FileMentions_ThatDoNotExist_AreLeftAsPlainText()
    {
        _ws.WriteProject("ghost.md", "Look at @nope/missing.cs there.");
        var catalog = _ws.Catalog();

        Assert.True(catalog.TryExpand("ghost", "", out var prompt, out _));

        Assert.Empty(prompt!.Files);
        Assert.Contains("@nope/missing.cs", prompt.Text);
        Assert.Contains(prompt.Diagnostics, d => d.Message.Contains("No such file"));
    }

    [Fact]
    public void OversizedReferencedFile_IsTruncatedBeforePromptConstruction()
    {
        _ws.WriteWorkspaceFile("big.txt", new string('a', 500));
        _ws.WriteProject("big.md", "Consider @big.txt.");
        var catalog = _ws.Catalog(new CustomCommandLimits { MaxReferencedFileBytes = 100 });

        Assert.True(catalog.TryExpand("big", "", out var prompt, out _));

        var part = Assert.Single(prompt!.Files);
        Assert.True(part.Truncated);
        Assert.Equal(100, part.Content.Length);
        Assert.Equal(500, part.FileBytes);
        Assert.Contains(prompt.Diagnostics, d => d.Message.Contains("Truncated"));
    }

    [Fact]
    public void ReferencedFileCount_IsCapped()
    {
        for (int i = 0; i < 5; i++)
            _ws.WriteWorkspaceFile($"f{i}.txt", "x");
        _ws.WriteProject("many.md", "@f0.txt @f1.txt @f2.txt @f3.txt @f4.txt");
        var catalog = _ws.Catalog(new CustomCommandLimits { MaxReferencedFiles = 2 });

        Assert.True(catalog.TryExpand("many", "", out var prompt, out _));

        Assert.Equal(2, prompt!.Files.Count);
        Assert.Contains(prompt.Diagnostics, d => d.Message.Contains("at most 2 file mentions"));
    }

    [Fact]
    public void EmailLikeText_IsNotTreatedAsAFileMention()
    {
        _ws.WriteProject("mail.md", "Ping someone@example.com about it.");
        var catalog = _ws.Catalog();

        Assert.True(catalog.TryExpand("mail", "", out var prompt, out _));

        Assert.Empty(prompt!.Files);
    }

    [Fact]
    public void Reload_PicksUpNewAndEditedFilesWithoutANewCatalog()
    {
        _ws.WriteProject("first.md", "First body.");
        var catalog = _ws.Catalog();
        Assert.Equal(new[] { "first" }, catalog.Commands.Select(c => c.Name).ToArray());

        _ws.WriteProject("second.md", "Second body.");
        _ws.WriteProject("first.md", "Edited body.");

        // Without a reload the cached snapshot is still served (that is the point of the cache).
        Assert.Equal(new[] { "first" }, catalog.Commands.Select(c => c.Name).ToArray());

        var reloaded = catalog.Reload();

        Assert.Equal(new[] { "first", "second" }, reloaded.Select(c => c.Name).ToArray());
        Assert.Equal("Edited body.", catalog.Find("first")!.Template);
    }

    [Fact]
    public void Reload_SeesDeletedCommands()
    {
        var path = _ws.WriteProject("gone.md", "Body.");
        var catalog = _ws.Catalog();
        Assert.Single(catalog.Commands);

        File.Delete(path);
        catalog.Invalidate();

        Assert.Empty(catalog.Commands);
    }

    [Fact]
    public void CustomResolver_CanReplaceTheBuiltInFileMentionResolver()
    {
        // The seam that issue #277's shared structured resolver plugs into.
        _ws.WriteProject("seam.md", "Body @anything.cs");
        var catalog = _ws.Catalog();
        catalog.FileResolver = new StubResolver();

        Assert.True(catalog.TryExpand("seam", "", out var prompt, out _));

        Assert.Equal("@stub", Assert.Single(prompt!.Files).Mention);
    }

    private sealed class StubResolver : ICustomCommandFileResolver
    {
        public System.Collections.Generic.IReadOnlyList<PromptFilePart> Resolve(
            string text,
            string workspaceDirectory,
            CustomCommandLimits limits,
            System.Collections.Generic.List<CustomCommandDiagnostic> diagnostics)
            => new[] { new PromptFilePart("@stub", "/stub", "stub content", 12, false) };
    }
}
