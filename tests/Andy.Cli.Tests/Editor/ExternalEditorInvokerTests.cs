using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Editor;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// The two entry points of issue #287 and, above all, the ordering that makes
/// <c>/editor</c> work: pressing Enter hands the text to the slash dispatcher and CLEARS the
/// composer, so the editor has to be seeded from a snapshot taken before that keystroke.
///
/// <para>Every <c>/editor</c> test here drives the real <see cref="PromptLine"/> through a
/// real Enter keystroke first, so the tests fail if the snapshot is ever taken after the
/// clear (the bug this class exists to pin).</para>
/// </summary>
public class ExternalEditorInvokerTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _writes = new();

    public ExternalEditorInvokerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "andy-invoker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    // ----- harness -----

    private sealed class StubRunner : IEditorProcessRunner
    {
        private readonly Func<string, EditorProcessResult> _behavior;
        public StubRunner(Func<string, EditorProcessResult> behavior) => _behavior = behavior;
        public string? SeenContents { get; private set; }
        public int Calls { get; private set; }

        public Task<EditorProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string filePath, CancellationToken ct)
        {
            Calls++;
            SeenContents = File.ReadAllText(filePath, Encoding.UTF8);
            return Task.FromResult(_behavior(filePath));
        }
    }

    private static StubRunner Saves(string contents, int exitCode = 0) =>
        new(path =>
        {
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return EditorProcessResult.Exited(exitCode);
        });

    private ExternalEditorService NewService(IEditorProcessRunner runner) =>
        new(new EditorResolver(_ => "fake-editor"),
            runner,
            new TerminalSuspendController(_writes.Add, null, () => _writes.Add("<repaint>")),
            _root);

    /// <summary>
    /// Reproduces the host's ordering exactly: snapshot the composer, then let PromptLine
    /// consume a real Enter (which submits and clears). Returns the snapshot the dispatcher
    /// would hand to /editor.
    /// </summary>
    private static (ComposerDocument Snapshot, string Submitted) SubmitEnter(
        PromptLine prompt, PromptLineComposer composer)
    {
        var snapshot = composer.GetDocument();
        var submitted = prompt.OnKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        Assert.NotNull(submitted);
        Assert.Equal("", prompt.Text); // the clear this whole class is about
        return (snapshot, submitted!);
    }

    private static (PromptLine Prompt, PromptLineComposer Composer) Composer(string text)
    {
        var prompt = new PromptLine();
        prompt.SetText(text);
        return (prompt, new PromptLineComposer(prompt));
    }

    private void AssertTerminalRestored()
    {
        Assert.Contains(TerminalSuspendController.LeaveTuiSequence, _writes);
        Assert.Contains(TerminalSuspendController.EnterTuiSequence, _writes);
        Assert.Contains("<repaint>", _writes);
    }

    // ----- /editor: the composer content reaches the editor -----

    [Fact]
    public async Task SlashEditor_OpensTheEditorOnTheTextThatWasInTheComposer()
    {
        var (prompt, composer) = Composer("/editor draft the migration plan");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var runner = Saves("done");
        var invoker = new ExternalEditorInvoker(NewService(runner), composer);

        await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal("draft the migration plan", runner.SeenContents);
    }

    [Fact]
    public async Task SlashEditor_KeepsMultilineTextAndUnicode()
    {
        var (prompt, composer) = Composer("/editor first\nsecond café 你好");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var runner = Saves("x");
        var invoker = new ExternalEditorInvoker(NewService(runner), composer);

        await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal("first\nsecond café 你好", runner.SeenContents);
    }

    [Fact]
    public async Task SlashEditor_AliasBehavesIdentically()
    {
        var (prompt, composer) = Composer("/edit some existing words");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var runner = Saves("x");
        var invoker = new ExternalEditorInvoker(NewService(runner), composer);

        await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal("some existing words", runner.SeenContents);
    }

    [Fact]
    public async Task SlashEditor_WithNothingAfterTheCommand_OpensAnEmptyBuffer()
    {
        var (prompt, composer) = Composer("/editor");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var runner = Saves("written from scratch");
        var invoker = new ExternalEditorInvoker(NewService(runner), composer);

        var result = await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal("", runner.SeenContents);
        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Equal("written from scratch", prompt.Text);
    }

    [Fact]
    public async Task SlashEditor_WithOnlyTrailingSpaces_OpensAnEmptyBuffer()
    {
        var (prompt, composer) = Composer("/editor    ");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var runner = Saves("");
        var invoker = new ExternalEditorInvoker(NewService(runner), composer);

        await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal("", runner.SeenContents);
    }

    // ----- /editor: the composer ends up holding exactly the edited content -----

    [Fact]
    public async Task SlashEditor_ReplacesTheComposer_WithoutDuplicatingThePreSubmitText()
    {
        var (prompt, composer) = Composer("/editor original draft");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var invoker = new ExternalEditorInvoker(NewService(Saves("final version")), composer);

        var result = await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Equal("final version", prompt.Text);
        Assert.DoesNotContain("original draft", prompt.Text);
        Assert.DoesNotContain("/editor", prompt.Text);
    }

    [Fact]
    public async Task SlashEditor_ClearingTheBuffer_LeavesAnEmptyComposer()
    {
        var (prompt, composer) = Composer("/editor delete all of this");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var invoker = new ExternalEditorInvoker(NewService(Saves("")), composer);

        var result = await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Equal("", prompt.Text);
    }

    // ----- /editor: failure paths must give the text back -----

    [Theory]
    [InlineData(1)]   // plain nonzero exit
    [InlineData(130)] // Ctrl+C reached the editor (SIGINT)
    [InlineData(137)] // SIGKILL
    public async Task SlashEditor_FailedEdit_RestoresTheOriginalComposerText(int exitCode)
    {
        var (prompt, composer) = Composer("/editor precious unsaved text");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var invoker = new ExternalEditorInvoker(NewService(Saves("must be discarded", exitCode)), composer);

        var result = await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal(ExternalEditorOutcome.EditorFailed, result.Outcome);
        Assert.Equal("precious unsaved text", prompt.Text);
        AssertTerminalRestored();
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task SlashEditor_LaunchFailure_RestoresTheOriginalComposerText()
    {
        var (prompt, composer) = Composer("/editor precious unsaved text");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var invoker = new ExternalEditorInvoker(
            NewService(new StubRunner(_ => EditorProcessResult.LaunchFailed("no such file."))), composer);

        var result = await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal(ExternalEditorOutcome.LaunchFailed, result.Outcome);
        Assert.Equal("precious unsaved text", prompt.Text);
        AssertTerminalRestored();
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task SlashEditor_NoEditorConfigured_RestoresTheOriginalComposerText()
    {
        var (prompt, composer) = Composer("/editor precious unsaved text");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var runner = Saves("unused");
        var service = new ExternalEditorService(
            new EditorResolver(_ => null),
            runner,
            new TerminalSuspendController(_writes.Add),
            _root);

        var result = await new ExternalEditorInvoker(service, composer).FromSubmittedCommandAsync(snapshot);

        Assert.Equal(ExternalEditorOutcome.NotConfigured, result.Outcome);
        Assert.Equal("precious unsaved text", prompt.Text);
        Assert.Equal(0, runner.Calls);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task SlashEditor_OversizedEdit_RestoresTheOriginalComposerText()
    {
        var (prompt, composer) = Composer("/editor precious unsaved text");
        var (snapshot, _) = SubmitEnter(prompt, composer);
        var service = new ExternalEditorService(
            new EditorResolver(_ => "fake-editor"),
            Saves(new string('x', 500)),
            new TerminalSuspendController(_writes.Add, null, () => _writes.Add("<repaint>")),
            _root,
            maxEditedBytes: 100);

        var result = await new ExternalEditorInvoker(service, composer).FromSubmittedCommandAsync(snapshot);

        Assert.Equal(ExternalEditorOutcome.TooLarge, result.Outcome);
        Assert.Equal("precious unsaved text", prompt.Text);
        AssertTerminalRestored();
    }

    // ----- /editor: structured parts -----

    [Fact]
    public async Task SlashEditor_PreservesStructuredParts_ThroughTheWholeRoundTrip()
    {
        var attachment = new ComposerAttachmentPart("@src/Program.cs", "file", "/repo/src/Program.cs", "payload");
        // What the composer would hold once #277 gives it real parts: the command token, then
        // text, then the attachment. The snapshot below is what the host captures pre-clear.
        var snapshot = new ComposerDocument(new ComposerPart[]
        {
            new ComposerTextPart("/editor review "),
            attachment,
            new ComposerTextPart(" carefully"),
        });
        var (prompt, composer) = Composer("");
        var runner = Saves("@src/Program.cs deserves a second look\n");
        var invoker = new ExternalEditorInvoker(NewService(runner), composer);

        var result = await invoker.FromSubmittedCommandAsync(snapshot);

        // The command token is gone; the attachment reached the editor as its placeholder.
        Assert.Equal("review @src/Program.cs carefully", runner.SeenContents);
        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        // ...and came back as the ORIGINAL record, not flattened text.
        var round = Assert.Single(result.Document.Attachments);
        Assert.Same(attachment, round);
        Assert.Equal("file", round.Kind);
        Assert.Equal("/repo/src/Program.cs", round.Reference);
        Assert.Equal("payload", round.Payload);
        Assert.Same(attachment, result.Document.Parts[0]);
    }

    [Fact]
    public async Task SlashEditor_FailedEdit_KeepsStructuredPartsInTheRestoredSeed()
    {
        var attachment = new ComposerAttachmentPart("@a.cs", "file", "/repo/a.cs", "payload");
        var snapshot = new ComposerDocument(new ComposerPart[]
        {
            new ComposerTextPart("/edit look at "),
            attachment,
        });
        var recorder = new RecordingComposer();
        var invoker = new ExternalEditorInvoker(NewService(Saves("clobbered", exitCode: 2)), recorder);

        var result = await invoker.FromSubmittedCommandAsync(snapshot);

        Assert.Equal(ExternalEditorOutcome.EditorFailed, result.Outcome);
        Assert.NotNull(recorder.Last);
        Assert.Same(attachment, Assert.Single(recorder.Last!.Attachments));
        Assert.Equal("look at @a.cs", recorder.Last!.ToPromptText());
    }

    private sealed class RecordingComposer : IComposerDocumentSource
    {
        public ComposerDocument Current { get; set; } = ComposerDocument.Empty;
        public ComposerDocument? Last { get; private set; }
        public int SetCalls { get; private set; }
        public ComposerDocument GetDocument() => Current;
        public void SetDocument(ComposerDocument document)
        {
            SetCalls++;
            Last = document;
            Current = document;
        }
    }

    [Fact]
    public async Task SlashEditor_WritesTheComposerExactlyOnce()
    {
        // Guards against a double-insert: one write, one document.
        var recorder = new RecordingComposer();
        var invoker = new ExternalEditorInvoker(NewService(Saves("edited")), recorder);

        await invoker.FromSubmittedCommandAsync(ComposerDocument.FromText("/editor seed"));

        Assert.Equal(1, recorder.SetCalls);
        Assert.Equal("edited", recorder.Last!.ToPromptText());
    }

    // ----- Ctrl+X path: unchanged guarantees -----

    [Fact]
    public async Task CtrlX_StartsFromTheLiveComposer()
    {
        var (prompt, composer) = Composer("typed but not submitted");
        var runner = Saves("edited");
        var invoker = new ExternalEditorInvoker(NewService(runner), composer);

        await invoker.FromComposerAsync();

        Assert.Equal("typed but not submitted", runner.SeenContents);
        Assert.Equal("edited", prompt.Text);
    }

    [Fact]
    public async Task CtrlX_FailedEdit_LeavesTheComposerUntouched()
    {
        var (prompt, composer) = Composer("typed but not submitted");
        var invoker = new ExternalEditorInvoker(NewService(Saves("discarded", exitCode: 1)), composer);

        var result = await invoker.FromComposerAsync();

        Assert.Equal(ExternalEditorOutcome.EditorFailed, result.Outcome);
        Assert.Equal("typed but not submitted", prompt.Text);
        AssertTerminalRestored();
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task CtrlX_FailedEdit_DoesNotWriteTheComposerAtAll()
    {
        var recorder = new RecordingComposer { Current = ComposerDocument.FromText("live text") };
        var invoker = new ExternalEditorInvoker(NewService(Saves("discarded", exitCode: 1)), recorder);

        await invoker.FromComposerAsync();

        Assert.Equal(0, recorder.SetCalls);
    }

    [Fact]
    public async Task BothEntryPoints_ProduceTheSameComposerForTheSameStartingText()
    {
        var (ctrlXPrompt, ctrlXComposer) = Composer("shared starting text");
        await new ExternalEditorInvoker(NewService(Saves("edited result")), ctrlXComposer).FromComposerAsync();

        var (slashPrompt, slashComposer) = Composer("/editor shared starting text");
        var (snapshot, _) = SubmitEnter(slashPrompt, slashComposer);
        await new ExternalEditorInvoker(NewService(Saves("edited result")), slashComposer)
            .FromSubmittedCommandAsync(snapshot);

        Assert.Equal(ctrlXPrompt.Text, slashPrompt.Text);
        Assert.Equal("edited result", slashPrompt.Text);
    }

    // ----- the token stripper -----

    [Theory]
    [InlineData("/editor hello", "hello")]
    [InlineData("/edit hello", "hello")]
    [InlineData("/editor   hello", "hello")]
    [InlineData("/editor\thello", "hello")]
    [InlineData("/editor", "")]
    [InlineData("/editor ", "")]
    [InlineData("/editor \n rest", " rest")]
    [InlineData("/editor a b  c", "a b  c")]
    [InlineData("/editor /not/a/command", "/not/a/command")]
    [InlineData("no slash at all", "no slash at all")]
    [InlineData("", "")]
    public void StripLeadingSlashCommand_RemovesOnlyTheCommandToken(string input, string expected)
        => Assert.Equal(expected,
            ExternalEditorInvoker.StripLeadingSlashCommand(ComposerDocument.FromText(input)).ToPromptText());

    [Fact]
    public void StripLeadingSlashCommand_NeverConsumesAStructuredPart()
    {
        var attachment = new ComposerAttachmentPart("@a.cs", "file", "/repo/a.cs");
        var doc = new ComposerDocument(new ComposerPart[] { new ComposerTextPart("/editor "), attachment });

        var stripped = ExternalEditorInvoker.StripLeadingSlashCommand(doc);

        Assert.Same(attachment, Assert.Single(stripped.Parts));
    }

    [Fact]
    public void StripLeadingSlashCommand_LeavesADocumentStartingWithAnAttachmentAlone()
    {
        var attachment = new ComposerAttachmentPart("@a.cs", "file", "/repo/a.cs");
        var doc = new ComposerDocument(new ComposerPart[] { attachment, new ComposerTextPart(" review") });

        var stripped = ExternalEditorInvoker.StripLeadingSlashCommand(doc);

        Assert.Same(attachment, stripped.Parts[0]);
        Assert.Equal("@a.cs review", stripped.ToPromptText());
    }

    [Fact]
    public void StripLeadingSlashCommand_OnAnEmptyDocument_IsEmpty()
        => Assert.True(ExternalEditorInvoker.StripLeadingSlashCommand(ComposerDocument.Empty).IsEmpty);

    [Fact]
    public void Constructor_RejectsNulls()
    {
        var service = NewService(Saves("x"));
        Assert.Throws<ArgumentNullException>(() => new ExternalEditorInvoker(null!, new RecordingComposer()));
        Assert.Throws<ArgumentNullException>(() => new ExternalEditorInvoker(service, null!));
    }

    [Fact]
    public async Task FromSubmittedCommand_ToleratesANullSnapshot()
    {
        var recorder = new RecordingComposer();
        var runner = Saves("from nothing");
        var invoker = new ExternalEditorInvoker(NewService(runner), recorder);

        var result = await invoker.FromSubmittedCommandAsync(null);

        Assert.Equal("", runner.SeenContents);
        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Equal("from nothing", recorder.Last!.ToPromptText());
    }
}
