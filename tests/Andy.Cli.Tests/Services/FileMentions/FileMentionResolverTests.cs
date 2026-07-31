using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Services.FileMentions;
using Andy.Model.Model;
using Xunit;

namespace Andy.Cli.Tests.Services.FileMentions;

public class FileMentionResolverTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private FileMentionResolver Resolver(FileMentionResolverOptions? options = null) =>
        new(_workspace.Root, new WorkspaceIgnoreRules(_workspace.Root), options);

    [Fact]
    public async Task ResolveAsync_NoMentions_ProducesJustTheUserText()
    {
        var resolved = await Resolver().ResolveAsync("plain prompt with no mentions");

        Assert.Empty(resolved.Attachments);
        Assert.False(resolved.HasAttachments);
        Assert.Equal("plain prompt with no mentions", resolved.ComposedText);
        Assert.Single(resolved.Parts);
    }

    [Fact]
    public async Task ResolveAsync_ExistingFile_AttachesContentAsStructuredPart()
    {
        _workspace.WriteFile("src/Foo.cs", "class Foo { }\n");

        var resolved = await Resolver().ResolveAsync("explain @src/Foo.cs please");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Equal("src/Foo.cs", attachment.RelativePath);
        Assert.Equal("class Foo { }\n", attachment.Content);
        Assert.Null(attachment.Range);

        Assert.Equal(2, resolved.Parts.Count);
        var parts = resolved.Parts.OfType<TextPart>().ToList();
        Assert.Equal("explain @src/Foo.cs please", parts[0].Text);
        Assert.Contains("<attached-file path=\"src/Foo.cs\">", parts[1].Text);
        Assert.Contains("class Foo { }", parts[1].Text);
        Assert.Contains("</attached-file>", parts[1].Text);
    }

    [Fact]
    public async Task ResolveAsync_ComposedText_IsTheFlattenedParts()
    {
        _workspace.WriteFile("a.txt", "alpha\n");

        var resolved = await Resolver().ResolveAsync("look @a.txt");

        string expected = string.Join("\n\n", resolved.Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.Equal(expected, resolved.ComposedText);
    }

    [Fact]
    public async Task ResolveAsync_LineRange_AttachesOnlyThoseLines()
    {
        _workspace.WriteFile("src/Foo.cs", "one\ntwo\nthree\nfour\nfive\n");

        var resolved = await Resolver().ResolveAsync("@src/Foo.cs#L2-L4");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Equal(new LineRange(2, 4), attachment.Range);
        Assert.Equal("two\nthree\nfour", attachment.Content);
        Assert.Contains("lines=\"2-4\"", resolved.ComposedText);
    }

    [Fact]
    public async Task ResolveAsync_BareNumericRange_IsEquivalentToLForm()
    {
        _workspace.WriteFile("src/Foo.cs", "one\ntwo\nthree\n");

        var withL = await Resolver().ResolveAsync("@src/Foo.cs#L2-L3");
        var bare = await Resolver().ResolveAsync("@src/Foo.cs#2-3");

        Assert.Equal(withL.Attachments[0].Content, bare.Attachments[0].Content);
        Assert.Equal(new LineRange(2, 3), bare.Attachments[0].Range);
    }

    [Fact]
    public async Task ResolveAsync_RangeEndPastEndOfFile_IsClampedToTheFile()
    {
        _workspace.WriteFile("short.txt", "one\ntwo\n");

        var resolved = await Resolver().ResolveAsync("@short.txt#L1-L99");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Equal(new LineRange(1, 2), attachment.Range);
    }

    [Fact]
    public async Task ResolveAsync_RangeStartPastEndOfFile_ReportsOutOfBounds()
    {
        _workspace.WriteFile("short.txt", "one\ntwo\n");

        var resolved = await Resolver().ResolveAsync("@short.txt#L40-L50");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.RangeOutOfBounds, attachment.Status);
        Assert.Null(attachment.Content);
        Assert.Contains("range-out-of-bounds", resolved.ComposedText);
    }

    [Fact]
    public async Task ResolveAsync_MissingFile_ReportsMissing()
    {
        var resolved = await Resolver().ResolveAsync("@does/not/exist.cs");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Missing, attachment.Status);
        Assert.Contains("status=\"missing\"", resolved.ComposedText);
    }

    [Fact]
    public async Task ResolveAsync_PathOutsideWorkspace_IsRefused()
    {
        var resolved = await Resolver().ResolveAsync("@../../etc/passwd");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.OutsideWorkspace, attachment.Status);
        Assert.Null(attachment.Content);
    }

    [Fact]
    public async Task ResolveAsync_AbsolutePathOutsideWorkspace_IsRefused()
    {
        string outside = Path.Combine(Path.GetTempPath(), "andy-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var resolved = await Resolver().ResolveAsync("@" + outside.Replace('\\', '/'));

            var attachment = Assert.Single(resolved.Attachments);
            Assert.Equal(FileMentionStatus.OutsideWorkspace, attachment.Status);
            Assert.DoesNotContain("secret", resolved.ComposedText);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task ResolveAsync_IgnoredFile_IsNotRead()
    {
        _workspace.WriteFile(".gitignore", "secrets.env\n");
        _workspace.WriteFile("secrets.env", "API_KEY=super-secret\n");

        var resolved = await Resolver().ResolveAsync("@secrets.env");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Ignored, attachment.Status);
        Assert.DoesNotContain("super-secret", resolved.ComposedText);
    }

    [Fact]
    public async Task ResolveAsync_FileInDefaultIgnoredDirectory_IsNotRead()
    {
        _workspace.WriteFile("node_modules/pkg/index.js", "module.exports = 1;\n");

        var resolved = await Resolver().ResolveAsync("@node_modules/pkg/index.js");

        Assert.Equal(FileMentionStatus.Ignored, resolved.Attachments[0].Status);
    }

    [Fact]
    public async Task ResolveAsync_BinaryFile_IsNotAttached()
    {
        _workspace.WriteBytes("assets/logo.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03 });

        var resolved = await Resolver().ResolveAsync("@assets/logo.png");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Binary, attachment.Status);
        Assert.Contains("status=\"binary\"", resolved.ComposedText);
    }

    [Fact]
    public async Task ResolveAsync_OversizedFile_IsRefusedWithGuidance()
    {
        _workspace.WriteLargeFile("big.log", 4096);
        var options = new FileMentionResolverOptions { MaxFileBytes = 1024 };

        var resolved = await Resolver(options).ResolveAsync("@big.log");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.TooLarge, attachment.Status);
        Assert.Contains("#L1-L200", attachment.Note);
    }

    [Fact]
    public async Task ResolveAsync_OversizedFileWithSmallRange_StillAttaches()
    {
        _workspace.WriteLargeFile("big.log", 4096);
        var options = new FileMentionResolverOptions { MaxFileBytes = 1024 };

        var resolved = await Resolver(options).ResolveAsync("@big.log#L1-L3");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Equal(new LineRange(1, 3), attachment.Range);
    }

    [Fact]
    public async Task ResolveAsync_Directory_IsNotAttached()
    {
        _workspace.CreateDirectory("src");

        var resolved = await Resolver().ResolveAsync("@src/");

        Assert.Equal(FileMentionStatus.Directory, resolved.Attachments[0].Status);
    }

    [Fact]
    public async Task ResolveAsync_DuplicateMention_AttachesContentOnlyOnce()
    {
        _workspace.WriteFile("a.txt", "alpha\n");

        var resolved = await Resolver().ResolveAsync("compare @a.txt with @a.txt");

        Assert.Equal(2, resolved.Attachments.Count);
        Assert.Equal(FileMentionStatus.Attached, resolved.Attachments[0].Status);
        Assert.Equal(FileMentionStatus.Duplicate, resolved.Attachments[1].Status);
        Assert.Single(resolved.AttachedFiles);
        // The repeated mention contributes no second block, so "alpha" is sent exactly once.
        Assert.Equal(1, resolved.ComposedText.Split("<attached-file").Length - 1);
        Assert.Equal(1, resolved.ComposedText.Split("alpha").Length - 1);
    }

    [Fact]
    public async Task ResolveAsync_DuplicatePathWithDifferentRanges_AttachesBoth()
    {
        _workspace.WriteFile("a.txt", "one\ntwo\nthree\nfour\n");

        var resolved = await Resolver().ResolveAsync("@a.txt#L1-L2 and @a.txt#L3-L4");

        Assert.Equal(2, resolved.AttachedFiles.Count);
        Assert.Equal("one\ntwo", resolved.AttachedFiles[0].Content);
        Assert.Equal("three\nfour", resolved.AttachedFiles[1].Content);
    }

    [Fact]
    public async Task ResolveAsync_SamePathWrittenDifferently_IsStillDeduplicated()
    {
        _workspace.WriteFile("src/Foo.cs", "x\n");

        var resolved = await Resolver().ResolveAsync(@"@src/Foo.cs and @./src/Foo.cs and @src\Foo.cs");

        Assert.Equal(3, resolved.Attachments.Count);
        Assert.Single(resolved.AttachedFiles);
        Assert.Equal(2, resolved.Attachments.Count(a => a.Status == FileMentionStatus.Duplicate));
    }

    [Fact]
    public async Task ResolveAsync_PathWithSpaces_RequiresQuotesAndAttaches()
    {
        _workspace.WriteFile("docs/my notes.md", "notes body\n");

        var resolved = await Resolver().ResolveAsync("read @\"docs/my notes.md\" now");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Equal("docs/my notes.md", attachment.RelativePath);
        Assert.Contains("notes body", resolved.ComposedText);
    }

    [Fact]
    public async Task ResolveAsync_UnquotedPathWithSpaces_OnlyTakesTheFirstWord()
    {
        _workspace.WriteFile("docs/my notes.md", "notes body\n");

        var resolved = await Resolver().ResolveAsync("read @docs/my notes.md");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Missing, attachment.Status);
        Assert.Equal("docs/my", attachment.RequestedPath);
    }

    [Fact]
    public async Task ResolveAsync_UnicodePath_Attaches()
    {
        _workspace.WriteFile("docs/café/日本語.md", "unicode body\n");

        var resolved = await Resolver().ResolveAsync("@docs/café/日本語.md");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Contains("unicode body", resolved.ComposedText);
    }

    [Fact]
    public async Task ResolveAsync_WindowsSeparators_ResolveToTheSameFile()
    {
        _workspace.WriteFile("src/Andy.Cli/Program.cs", "program body\n");

        var resolved = await Resolver().ResolveAsync(@"@src\Andy.Cli\Program.cs");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Equal("src/Andy.Cli/Program.cs", attachment.RelativePath);
    }

    [Fact]
    public async Task ResolveAsync_FileNameContainingHash_PrefersTheLiteralFile()
    {
        _workspace.WriteFile("notes#12", "hash file body\n");

        var resolved = await Resolver().ResolveAsync("@notes#12");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Equal("notes#12", attachment.RelativePath);
        Assert.Null(attachment.Range);
    }

    [Fact]
    public async Task ResolveAsync_RangeWinsWhenNoLiteralHashFileExists()
    {
        _workspace.WriteFile("notes", "one\ntwo\nthree\n");

        var resolved = await Resolver().ResolveAsync("@notes#2");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Equal(new LineRange(2, 2), attachment.Range);
        Assert.Equal("two", attachment.Content);
    }

    [Fact]
    public async Task ResolveAsync_QuotedPathWithHash_ReadsTheLiteralFile()
    {
        _workspace.WriteFile("docs/rev#12.md", "revision body\n");

        var resolved = await Resolver().ResolveAsync("@\"docs/rev#12.md\"");

        var attachment = Assert.Single(resolved.Attachments);
        Assert.Equal(FileMentionStatus.Attached, attachment.Status);
        Assert.Equal("docs/rev#12.md", attachment.RelativePath);
    }

    [Fact]
    public async Task ResolveAsync_AttachmentCountBudget_StopsAttachingBeyondTheLimit()
    {
        for (int i = 0; i < 4; i++)
        {
            _workspace.WriteFile($"f{i}.txt", $"body {i}\n");
        }
        var options = new FileMentionResolverOptions { MaxAttachments = 2 };

        var resolved = await Resolver(options).ResolveAsync("@f0.txt @f1.txt @f2.txt @f3.txt");

        Assert.Equal(2, resolved.AttachedFiles.Count);
        Assert.Equal(2, resolved.Attachments.Count(a => a.Status == FileMentionStatus.BudgetExceeded));
    }

    [Fact]
    public async Task ResolveAsync_TotalSizeBudget_StopsAttachingBeyondTheLimit()
    {
        _workspace.WriteLargeFile("a.txt", 2048);
        _workspace.WriteLargeFile("b.txt", 2048);
        var options = new FileMentionResolverOptions { MaxFileBytes = 8192, MaxTotalBytes = 1024 };

        var resolved = await Resolver(options).ResolveAsync("@a.txt @b.txt");

        Assert.Single(resolved.AttachedFiles);
        Assert.Equal(FileMentionStatus.BudgetExceeded, resolved.Attachments[1].Status);
    }

    [Fact]
    public async Task ResolveAsync_EscapesMarkupInPathsAndNotes()
    {
        var resolved = await Resolver().ResolveAsync("@a\"b<c>.txt");

        Assert.Contains("&quot;", resolved.ComposedText);
        Assert.Contains("&lt;", resolved.ComposedText);
    }

    [Fact]
    public async Task DescribeResolution_ReportsAttachedAndSkippedFiles()
    {
        _workspace.WriteFile("a.txt", "alpha\n");

        var resolved = await Resolver().ResolveAsync("@a.txt @missing.txt");
        string? note = FileMentionSession.DescribeResolution(resolved);

        Assert.NotNull(note);
        Assert.Contains("Attached 1 file: a.txt", note);
        Assert.Contains("Skipped missing.txt", note);
    }

    [Fact]
    public async Task DescribeResolution_WithoutMentions_ReturnsNull()
    {
        var resolved = await Resolver().ResolveAsync("no mentions here");
        Assert.Null(FileMentionSession.DescribeResolution(resolved));
    }
}
