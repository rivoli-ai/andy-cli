using System.Linq;
using Andy.Cli.Editor;
using Xunit;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// The composer round trip used by the external editor (issue #287).
///
/// The acceptance criterion that matters most here is that structured <c>@file</c> parts
/// (and future image parts, issue #277) survive an edit: they must come back as the SAME
/// record, not as text that happens to look the same.
/// </summary>
public class ComposerDocumentTests
{
    private static ComposerAttachmentPart File(string placeholder, string reference, string? payload = null)
        => new(placeholder, "file", reference, payload);

    [Fact]
    public void FromText_ProducesASingleTextPart()
    {
        var doc = ComposerDocument.FromText("hello");

        Assert.Equal("hello", doc.ToEditableText());
        var part = Assert.Single(doc.Parts);
        Assert.Equal(new ComposerTextPart("hello"), part);
    }

    [Fact]
    public void EmptyText_ProducesAnEmptyDocument()
    {
        var doc = ComposerDocument.FromText("");

        Assert.True(doc.IsEmpty);
        Assert.Empty(doc.Parts);
        Assert.Equal("", doc.ToEditableText());
    }

    [Fact]
    public void FromText_NormalizesLineEndings()
    {
        var doc = ComposerDocument.FromText("a\r\nb\rc\nd");
        Assert.Equal("a\nb\nc\nd", doc.ToEditableText());
    }

    [Fact]
    public void EditablePreservesNewlinesAndUnicode()
    {
        const string text = "line one\n\nline three\n  indented\ncafé éü 你好 \U0001F600 مرحبا";

        var edited = ComposerDocument.FromText("x").ApplyEditedText(text);

        Assert.Equal(text, edited.ToEditableText());
        Assert.Equal(text, edited.ToPromptText());
    }

    [Fact]
    public void ApplyEditedText_ToEmpty_ClearsTheDocument()
    {
        var edited = ComposerDocument.FromText("something").ApplyEditedText("");

        Assert.True(edited.IsEmpty);
        Assert.Equal("", edited.ToPromptText());
    }

    // ----- structured parts -----

    [Fact]
    public void Attachments_AreRenderedAsPlaceholdersForTheEditor()
    {
        var doc = new ComposerDocument(new ComposerPart[]
        {
            new ComposerTextPart("review "),
            File("@src/Program.cs", "/repo/src/Program.cs", "payload"),
            new ComposerTextPart(" please"),
        });

        Assert.Equal("review @src/Program.cs please", doc.ToEditableText());
    }

    [Fact]
    public void UnchangedEdit_ReturnsTheIdenticalAttachmentInstances()
    {
        var attachment = File("@src/Program.cs", "/repo/src/Program.cs", "payload");
        var doc = new ComposerDocument(new ComposerPart[]
        {
            new ComposerTextPart("review "),
            attachment,
            new ComposerTextPart(" please"),
        });

        var edited = doc.ApplyEditedText(doc.ToEditableText());

        var round = Assert.Single(edited.Attachments);
        Assert.Same(attachment, round);
        Assert.Equal("file", round.Kind);
        Assert.Equal("/repo/src/Program.cs", round.Reference);
        Assert.Equal("payload", round.Payload);
    }

    [Fact]
    public void MovedPlaceholder_KeepsTheStructuredPart_AtItsNewPosition()
    {
        var attachment = File("@notes.md", "/repo/notes.md");
        var doc = new ComposerDocument(new ComposerPart[]
        {
            new ComposerTextPart("first "),
            attachment,
        });

        var edited = doc.ApplyEditedText("@notes.md now leads\nand a second line");

        Assert.Same(attachment, edited.Parts[0]);
        Assert.Equal(new ComposerTextPart(" now leads\nand a second line"), edited.Parts[1]);
    }

    [Fact]
    public void RewritingSurroundingText_DoesNotFlattenTheAttachment()
    {
        var attachment = File("@a.cs", "/repo/a.cs", "bytes");
        var doc = new ComposerDocument(new ComposerPart[] { new ComposerTextPart("old "), attachment });

        var edited = doc.ApplyEditedText("completely rewritten prompt about @a.cs with more detail");

        Assert.Same(attachment, Assert.Single(edited.Attachments));
        Assert.Equal(3, edited.Parts.Count);
        Assert.Equal("completely rewritten prompt about @a.cs with more detail", edited.ToPromptText());
    }

    [Fact]
    public void MultipleAttachments_KeepTheirOrderAndIdentity()
    {
        var a = File("@a.cs", "/repo/a.cs");
        var b = File("@b.cs", "/repo/b.cs");
        var doc = new ComposerDocument(new ComposerPart[] { a, new ComposerTextPart(" and "), b });

        var edited = doc.ApplyEditedText("compare @b.cs against @a.cs");

        Assert.Collection(edited.Attachments,
            first => Assert.Same(b, first),
            second => Assert.Same(a, second));
    }

    [Fact]
    public void DeletedPlaceholder_DropsOnlyThatAttachment()
    {
        var a = File("@a.cs", "/repo/a.cs");
        var b = File("@b.cs", "/repo/b.cs");
        var doc = new ComposerDocument(new ComposerPart[] { a, new ComposerTextPart(" "), b });

        var edited = doc.ApplyEditedText("just @b.cs");

        Assert.Same(b, Assert.Single(edited.Attachments));
    }

    [Fact]
    public void DuplicatedPlaceholder_ReusesTheSameAttachmentRecord()
    {
        var a = File("@a.cs", "/repo/a.cs", "bytes");
        var doc = new ComposerDocument(new ComposerPart[] { a });

        var edited = doc.ApplyEditedText("@a.cs and again @a.cs");

        Assert.Equal(2, edited.Attachments.Count);
        Assert.All(edited.Attachments, x => Assert.Equal("/repo/a.cs", x.Reference));
        Assert.Same(a, edited.Attachments[0]);
    }

    [Fact]
    public void LongestPlaceholderWins_WhenOneIsAPrefixOfAnother()
    {
        var shortPart = File("@src", "/repo/src");
        var longPart = File("@src/a.cs", "/repo/src/a.cs");
        var doc = new ComposerDocument(new ComposerPart[] { shortPart, longPart });

        var edited = doc.ApplyEditedText("look at @src/a.cs");

        Assert.Same(longPart, Assert.Single(edited.Attachments));
    }

    [Fact]
    public void ImagePartsRideAlongOnTheSameMechanism()
    {
        // #277 will add image parts; nothing in the round trip is file specific.
        var image = new ComposerAttachmentPart("[image #1]", "image", "clipboard:0", "base64-bytes");
        var doc = new ComposerDocument(new ComposerPart[] { new ComposerTextPart("what is "), image });

        var edited = doc.ApplyEditedText("explain [image #1] in detail");

        var round = Assert.Single(edited.Attachments);
        Assert.Same(image, round);
        Assert.Equal("image", round.Kind);
        Assert.Equal("base64-bytes", round.Payload);
    }

    [Fact]
    public void EmptyTextParts_AreNeverStored()
    {
        var doc = new ComposerDocument(new ComposerPart[]
        {
            new ComposerTextPart(""),
            File("@a.cs", "/repo/a.cs"),
            new ComposerTextPart(""),
        });

        Assert.Single(doc.Parts);
        Assert.All(doc.Parts, p => Assert.IsType<ComposerAttachmentPart>(p));
    }

    [Fact]
    public void AttachmentsWithoutAnyEditedOccurrence_ProduceAPlainTextDocument()
    {
        var doc = new ComposerDocument(new ComposerPart[] { File("@a.cs", "/repo/a.cs") });

        var edited = doc.ApplyEditedText("no references at all");

        Assert.Empty(edited.Attachments);
        Assert.Equal("no references at all", edited.ToPromptText());
        Assert.Equal(new ComposerTextPart("no references at all"), Assert.Single(edited.Parts));
    }
}
