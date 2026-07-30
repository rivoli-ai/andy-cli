using System;
using System.Threading.Tasks;
using Andy.Cli.Editor;
using Andy.Cli.Input;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// The adapter between the interactive composer and the external-editor pipeline
/// (issue #287), plus the invariants the host relies on.
/// </summary>
public class PromptLineComposerTests
{
    [Fact]
    public void GetDocument_SnapshotsTheComposerText()
    {
        var prompt = new PromptLine();
        prompt.SetText("hello\nworld");

        var doc = new PromptLineComposer(prompt).GetDocument();

        Assert.Equal("hello\nworld", doc.ToEditableText());
    }

    [Fact]
    public void GetDocument_OnAnEmptyComposer_IsAnEmptyDocument()
    {
        var doc = new PromptLineComposer(new PromptLine()).GetDocument();

        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public void SetDocument_ReplacesTheComposerText()
    {
        var prompt = new PromptLine();
        prompt.SetText("before");
        var composer = new PromptLineComposer(prompt);

        composer.SetDocument(ComposerDocument.FromText("after\nedit"));

        Assert.Equal("after\nedit", prompt.Text);
    }

    [Fact]
    public void SetDocument_WithAnEmptyDocument_ClearsTheComposer()
    {
        var prompt = new PromptLine();
        prompt.SetText("before");

        new PromptLineComposer(prompt).SetDocument(ComposerDocument.Empty);

        Assert.Equal("", prompt.Text);
    }

    [Fact]
    public void SetDocument_RendersAttachmentsAsTheirPlaceholders()
    {
        // Until #277 lands the composer is a plain string, so structured parts must at least
        // round trip through their placeholder text without being dropped.
        var prompt = new PromptLine();
        var doc = new ComposerDocument(new ComposerPart[]
        {
            new ComposerTextPart("review "),
            new ComposerAttachmentPart("@src/Program.cs", "file", "/repo/src/Program.cs"),
        });

        new PromptLineComposer(prompt).SetDocument(doc);

        Assert.Equal("review @src/Program.cs", prompt.Text);
    }

    [Fact]
    public void ComposerRoundTrip_ThroughTheService_UpdatesTheWidget()
    {
        var prompt = new PromptLine();
        prompt.SetText("draft");
        var composer = new PromptLineComposer(prompt);

        var edited = composer.GetDocument().ApplyEditedText("final version");
        composer.SetDocument(edited);

        Assert.Equal("final version", prompt.Text);
    }

    [Fact]
    public void NullComposer_IsRejected()
        => Assert.Throws<ArgumentNullException>(() => new PromptLineComposer(null!));

    [Fact]
    public void RawTerminalInput_IsUsableAsTheSuspendableInput()
    {
        // The host passes the live RawTerminalInput straight into the controller; this pins
        // that the type keeps implementing the contract the editor hand-off relies on.
        Assert.True(typeof(ISuspendableTerminalInput).IsAssignableFrom(typeof(RawTerminalInput)));
    }

    [Fact]
    public void RawTerminalInput_TryStart_ReturnsNullWithoutATty()
    {
        // Under the test host stdin is redirected, so there is nothing to suspend and the
        // controller must tolerate a null input (covered in TerminalSuspendControllerTests).
        Assert.Null(RawTerminalInput.TryStart());
    }
}
