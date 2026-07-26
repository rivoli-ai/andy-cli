using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Editor;
using Xunit;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// The full external-editor round trip (issue #287) driven by a deterministic in-process
/// stub editor. Covers which outcomes replace the composer, which leave it untouched, and
/// that the terminal and the temporary file are cleaned up on every single path.
/// </summary>
public class ExternalEditorServiceTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _writes = new();
    private readonly FakeInput _input = new();

    public ExternalEditorServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "andy-editor-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    // ----- fakes -----

    private sealed class FakeInput : ISuspendableTerminalInput
    {
        public int SuspendCalls;
        public int ResumeCalls;
        public bool IsSuspended => SuspendCalls > ResumeCalls;
        public IDisposable Suspend() { SuspendCalls++; return new Scope(this); }
        private sealed class Scope : IDisposable
        {
            private readonly FakeInput _o; private bool _d;
            public Scope(FakeInput o) => _o = o;
            public void Dispose() { if (_d) return; _d = true; _o.ResumeCalls++; }
        }
    }

    /// <summary>
    /// Stands in for the editor process. Records the launch it was asked to perform and
    /// applies a scripted effect to the temp file.
    /// </summary>
    private sealed class StubRunner : IEditorProcessRunner
    {
        private readonly Func<string, EditorProcessResult> _behavior;
        public StubRunner(Func<string, EditorProcessResult> behavior) => _behavior = behavior;

        public string? FileName { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
        public string? FilePath { get; private set; }
        public string? SeenContents { get; private set; }
        public int Calls { get; private set; }

        public Task<EditorProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string filePath, CancellationToken ct)
        {
            Calls++;
            FileName = fileName;
            Arguments = arguments.ToArray();
            FilePath = filePath;
            SeenContents = File.ReadAllText(filePath, Encoding.UTF8);
            return Task.FromResult(_behavior(filePath));
        }
    }

    private static IEditorProcessRunner Saves(string newContents, int exitCode = 0) =>
        new StubRunner(path =>
        {
            File.WriteAllText(path, newContents, new UTF8Encoding(false));
            return EditorProcessResult.Exited(exitCode);
        });

    private ExternalEditorService NewService(
        IEditorProcessRunner runner,
        string? visual = "fake-editor",
        string? editor = null,
        int maxBytes = ExternalEditorService.DefaultMaxEditedBytes)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (visual is not null) env["VISUAL"] = visual;
        if (editor is not null) env["EDITOR"] = editor;

        var terminal = new TerminalSuspendController(_writes.Add, _input, () => _writes.Add("<repaint>"));
        return new ExternalEditorService(
            new EditorResolver(n => env.TryGetValue(n, out var v) ? v : null),
            runner,
            terminal,
            _root,
            maxBytes);
    }

    private void AssertTerminalFullyRestored()
    {
        Assert.Contains(TerminalSuspendController.LeaveTuiSequence, _writes);
        Assert.Contains(TerminalSuspendController.EnterTuiSequence, _writes);
        Assert.Contains("<repaint>", _writes);
        Assert.False(_input.IsSuspended);
        Assert.Equal(_input.SuspendCalls, _input.ResumeCalls);
        Assert.Equal(1, _input.SuspendCalls);
    }

    private void AssertNoTempFilesLeft()
        => Assert.Empty(Directory.GetFileSystemEntries(_root));

    // ----- success -----

    [Fact]
    public async Task SuccessfulEdit_ReplacesTheComposer_AndRestoresEverything()
    {
        var runner = Saves("the edited prompt\n");
        var service = NewService(runner);

        var result = await service.EditAsync(ComposerDocument.FromText("original"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.True(result.Applied);
        Assert.Equal("the edited prompt", result.Document.ToPromptText());
        AssertTerminalFullyRestored();
        AssertNoTempFilesLeft();
    }

    [Fact]
    public async Task TheEditorSeesTheCurrentComposerText()
    {
        var runner = new StubRunner(_ => EditorProcessResult.Exited(0));
        var service = NewService(runner);

        await service.EditAsync(ComposerDocument.FromText("hello\nworld"));

        Assert.Equal("hello\nworld", runner.SeenContents);
    }

    [Fact]
    public async Task NewlinesAndUnicode_SurviveTheRoundTrip()
    {
        const string edited = "first\n\nthird line\n  indented\ncafé éü 你好 \U0001F600 مرحبا\n";
        var service = NewService(Saves(edited));

        var result = await service.EditAsync(ComposerDocument.FromText("x"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        // Exactly one editor-added trailing newline is dropped; everything else is verbatim.
        Assert.Equal("first\n\nthird line\n  indented\ncafé éü 你好 \U0001F600 مرحبا", result.Document.ToPromptText());
    }

    [Fact]
    public async Task DeliberatelyBlankLastLine_IsKept()
    {
        var service = NewService(Saves("body\n\n"));

        var result = await service.EditAsync(ComposerDocument.FromText("x"));

        Assert.Equal("body\n", result.Document.ToPromptText());
    }

    [Fact]
    public async Task IntentionallyEmptyPrompt_IsAppliedAsAnEmptyComposer()
    {
        var service = NewService(Saves(""));

        var result = await service.EditAsync(ComposerDocument.FromText("delete me"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.True(result.Document.IsEmpty);
        Assert.Equal("", result.Document.ToPromptText());
    }

    [Fact]
    public async Task EmptyPromptIn_EmptyPromptOut()
    {
        var runner = new StubRunner(_ => EditorProcessResult.Exited(0));
        var service = NewService(runner);

        var result = await service.EditAsync(ComposerDocument.Empty);

        Assert.Equal("", runner.SeenContents);
        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.True(result.Document.IsEmpty);
    }

    [Fact]
    public async Task CrlfWrittenByTheEditor_IsNormalized()
    {
        var service = NewService(Saves("a\r\nb\r\n"));

        var result = await service.EditAsync(ComposerDocument.FromText("x"));

        Assert.Equal("a\nb", result.Document.ToPromptText());
    }

    // ----- structured parts -----

    [Fact]
    public async Task StructuredParts_AreNotFlattenedOrLost()
    {
        var attachment = new ComposerAttachmentPart("@src/Program.cs", "file", "/repo/src/Program.cs", "payload");
        var document = new ComposerDocument(new ComposerPart[]
        {
            new ComposerTextPart("review "),
            attachment,
        });

        var runner = new StubRunner(path =>
        {
            // The editor only ever sees the placeholder, and moves it.
            File.WriteAllText(path, "@src/Program.cs needs a closer look\n", new UTF8Encoding(false));
            return EditorProcessResult.Exited(0);
        });
        var service = NewService(runner);

        var result = await service.EditAsync(document);

        Assert.Equal("review @src/Program.cs", runner.SeenContents);
        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        var round = Assert.Single(result.Document.Attachments);
        Assert.Same(attachment, round);
        Assert.Equal("file", round.Kind);
        Assert.Equal("/repo/src/Program.cs", round.Reference);
        Assert.Equal("payload", round.Payload);
        Assert.Same(attachment, result.Document.Parts[0]);
    }

    [Fact]
    public async Task StructuredParts_AreUntouchedWhenTheEditorFails()
    {
        var attachment = new ComposerAttachmentPart("@a.cs", "file", "/repo/a.cs", "payload");
        var document = new ComposerDocument(new ComposerPart[] { attachment });
        var service = NewService(Saves("clobbered", exitCode: 1));

        var result = await service.EditAsync(document);

        Assert.Equal(ExternalEditorOutcome.EditorFailed, result.Outcome);
        Assert.Same(document, result.Document);
        Assert.Same(attachment, Assert.Single(result.Document.Attachments));
    }

    // ----- failure paths: composer must stay unchanged -----

    [Fact]
    public async Task NonzeroExit_LeavesTheComposerUnchanged_AndStillRestoresTheTerminal()
    {
        var original = ComposerDocument.FromText("keep me");
        var service = NewService(Saves("would have replaced it", exitCode: 1));

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.EditorFailed, result.Outcome);
        Assert.False(result.Applied);
        Assert.Same(original, result.Document);
        Assert.Contains("exited with code 1", result.Message);
        AssertTerminalFullyRestored();
        AssertNoTempFilesLeft();
    }

    [Theory]
    [InlineData(130, 2)]  // SIGINT (Ctrl+C reached the editor)
    [InlineData(137, 9)]  // SIGKILL
    [InlineData(143, 15)] // SIGTERM
    public async Task ChildKilledBySignal_LeavesTheComposerUnchanged_AndStillRestoresTheTerminal(int exitCode, int signal)
    {
        var original = ComposerDocument.FromText("keep me");
        var service = NewService(Saves("clobbered", exitCode));

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.EditorFailed, result.Outcome);
        Assert.Same(original, result.Document);
        Assert.Contains($"terminated by signal {signal}", result.Message);
        AssertTerminalFullyRestored();
        AssertNoTempFilesLeft();
    }

    [Fact]
    public async Task LaunchFailure_LeavesTheComposerUnchanged_AndStillRestoresTheTerminal()
    {
        var original = ComposerDocument.FromText("keep me");
        var service = NewService(new StubRunner(_ => EditorProcessResult.LaunchFailed("no such file or directory.")));

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.LaunchFailed, result.Outcome);
        Assert.Same(original, result.Document);
        Assert.Contains("no such file", result.Message);
        Assert.Contains(EditorSetupGuidance.DocsPath, result.Message);
        AssertTerminalFullyRestored();
        AssertNoTempFilesLeft();
    }

    [Fact]
    public async Task RunnerThrowing_LeavesTheComposerUnchanged_AndStillRestoresTheTerminal()
    {
        var original = ComposerDocument.FromText("keep me");
        var service = NewService(new ThrowingRunner(new InvalidOperationException("boom")));

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.Error, result.Outcome);
        Assert.Same(original, result.Document);
        AssertTerminalFullyRestored();
        AssertNoTempFilesLeft();
    }

    private sealed class ThrowingRunner : IEditorProcessRunner
    {
        private readonly Exception _ex;
        public ThrowingRunner(Exception ex) => _ex = ex;
        public Task<EditorProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string filePath, CancellationToken ct)
            => throw _ex;
    }

    [Fact]
    public async Task Cancellation_LeavesTheComposerUnchanged_AndStillRestoresTheTerminal()
    {
        var original = ComposerDocument.FromText("keep me");
        using var cts = new CancellationTokenSource();
        var service = NewService(new ThrowingRunner(new OperationCanceledException()));

        var result = await service.EditAsync(original, cts.Token);

        Assert.Equal(ExternalEditorOutcome.Cancelled, result.Outcome);
        Assert.Same(original, result.Document);
        AssertTerminalFullyRestored();
        AssertNoTempFilesLeft();
    }

    [Fact]
    public async Task OversizedEdit_IsRejected_AndTheComposerIsUnchanged()
    {
        var original = ComposerDocument.FromText("keep me");
        var service = NewService(Saves(new string('x', 200)), maxBytes: 100);

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.TooLarge, result.Outcome);
        Assert.Same(original, result.Document);
        Assert.Contains("100 byte limit", result.Message);
        AssertTerminalFullyRestored();
        AssertNoTempFilesLeft();
    }

    [Fact]
    public async Task EditAtExactlyTheLimit_IsAccepted()
    {
        var service = NewService(Saves(new string('x', 100)), maxBytes: 100);

        var result = await service.EditAsync(ComposerDocument.FromText("x"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
    }

    // ----- not configured -----

    [Fact]
    public async Task NoEditorConfigured_ShowsGuidance_WithoutTouchingTheTerminalOrDisk()
    {
        var runner = new StubRunner(_ => EditorProcessResult.Exited(0));
        var original = ComposerDocument.FromText("keep me");
        var service = NewService(runner, visual: null, editor: null);

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.NotConfigured, result.Outcome);
        Assert.Same(original, result.Document);
        Assert.Contains("export VISUAL=", result.Message);
        Assert.Equal(0, runner.Calls);
        Assert.Empty(_writes);                 // the TUI never gave up the terminal
        Assert.Equal(0, _input.SuspendCalls);
        AssertNoTempFilesLeft();               // and no temp file was created
    }

    [Fact]
    public async Task VisualTakesPrecedenceOverEditor_WhenLaunching()
    {
        var runner = new StubRunner(_ => EditorProcessResult.Exited(0));
        var service = NewService(runner, visual: "visual-editor --wait", editor: "editor-editor");

        await service.EditAsync(ComposerDocument.FromText("x"));

        Assert.Equal("visual-editor", runner.FileName);
        Assert.Equal(new[] { "--wait" }, runner.Arguments.ToArray());
    }

    [Fact]
    public async Task ConfiguredArguments_ArePassedBeforeTheFilePath()
    {
        var runner = new StubRunner(_ => EditorProcessResult.Exited(0));
        var service = NewService(runner, visual: "\"/opt/my editor/bin/edit\" --wait -n");

        await service.EditAsync(ComposerDocument.FromText("x"));

        Assert.Equal("/opt/my editor/bin/edit", runner.FileName);
        Assert.Equal(new[] { "--wait", "-n" }, runner.Arguments.ToArray());
        Assert.Equal(runner.FilePath, Path.Combine(Path.GetDirectoryName(runner.FilePath)!, "andy-prompt.md"));
    }

    // ----- trailing newline helper -----

    [Theory]
    [InlineData("a\n", "a")]
    [InlineData("a", "a")]
    [InlineData("a\n\n", "a\n")]
    [InlineData("\n", "")]
    [InlineData("", "")]
    [InlineData("a\r\n", "a")]
    public void TrimEditorTrailingNewline_DropsExactlyOne(string input, string expected)
        => Assert.Equal(expected, ExternalEditorService.TrimEditorTrailingNewline(input));
}
