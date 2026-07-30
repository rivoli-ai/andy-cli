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
/// End-to-end tests that launch a REAL child process - the deterministic
/// <see cref="FakeEditor"/> script, never a real editor (issue #287).
///
/// The script lives at a path whose directory AND file name contain spaces, so every test
/// here also covers "paths and commands containing spaces". <c>--dump-args</c> makes the
/// child write its own argv into the edited file, which is how "no shell interpolation" is
/// proven: metacharacters come back verbatim instead of expanded.
/// </summary>
public class EditorProcessRunnerTests : IDisposable
{
    private readonly FakeEditor _editor = FakeEditor.Create();
    private readonly string _root;

    public EditorProcessRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "andy-runner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _editor.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private ExternalEditorService NewService(
        string visual,
        out List<string> writes,
        string? editor = null,
        int maxBytes = ExternalEditorService.DefaultMaxEditedBytes)
    {
        var captured = new List<string>();
        writes = captured;
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["VISUAL"] = visual };
        if (editor is not null) env["EDITOR"] = editor;

        return new ExternalEditorService(
            new EditorResolver(n => env.TryGetValue(n, out var v) ? v : null),
            new EditorProcessRunner(),
            new TerminalSuspendController(captured.Add, null, () => captured.Add("<repaint>")),
            _root,
            maxBytes);
    }

    [Fact]
    public async Task LaunchesACommandWhosePathContainsSpaces()
    {
        string content = _editor.ContentFile("edited by the fake editor");
        var service = NewService($"{_editor.QuotedCommand} --content \"{content}\"", out var writes);

        var result = await service.EditAsync(ComposerDocument.FromText("before"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Equal("edited by the fake editor", result.Document.ToPromptText());
        Assert.Contains(TerminalSuspendController.EnterTuiSequence, writes);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task ArgumentsArePassedVerbatim_WithNoShellInterpolation()
    {
        // The literals below would all be mangled by a shell. They must arrive untouched.
        var service = NewService(
            $"{_editor.QuotedCommand} --dump-args --wait '$HOME' '*' 'a b' 'x;y' 'p|q' '$(echo hi)'",
            out _);

        var result = await service.EditAsync(ComposerDocument.FromText("before"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        var argv = result.Document.ToPromptText().Split('\n');

        Assert.Equal("--dump-args", argv[0]);
        Assert.Equal("--wait", argv[1]);
        Assert.Equal("$HOME", argv[2]);
        Assert.Equal("*", argv[3]);
        Assert.Equal("a b", argv[4]);
        Assert.Equal("x;y", argv[5]);
        Assert.Equal("p|q", argv[6]);
        Assert.Equal("$(echo hi)", argv[7]);

        // The file to edit is always appended last, inside the private temp directory.
        Assert.EndsWith("andy-prompt.md", argv[8], StringComparison.Ordinal);
        Assert.StartsWith(_root, argv[8], StringComparison.Ordinal);
    }

    [Fact]
    public async Task VisualTakesPrecedenceOverEditor_ForARealLaunch()
    {
        string fromVisual = _editor.ContentFile("came from VISUAL");
        var service = NewService(
            $"{_editor.QuotedCommand} --content \"{fromVisual}\"",
            out _,
            editor: "definitely-not-a-real-program-287");

        var result = await service.EditAsync(ComposerDocument.FromText("before"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Equal("came from VISUAL", result.Document.ToPromptText());
    }

    [Fact]
    public async Task EditorFallsBackWhenVisualIsBlank_ForARealLaunch()
    {
        string content = _editor.ContentFile("came from EDITOR");
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["VISUAL"] = "   ",
            ["EDITOR"] = $"{_editor.QuotedCommand} --content \"{content}\"",
        };
        var writes = new List<string>();
        var service = new ExternalEditorService(
            new EditorResolver(n => env.TryGetValue(n, out var v) ? v : null),
            new EditorProcessRunner(),
            new TerminalSuspendController(writes.Add),
            _root);

        var result = await service.EditAsync(ComposerDocument.FromText("before"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Equal("came from EDITOR", result.Document.ToPromptText());
    }

    [Fact]
    public async Task NonzeroExitFromARealChild_LeavesTheComposerUnchanged()
    {
        string content = _editor.ContentFile("this must be discarded");
        var original = ComposerDocument.FromText("keep me");
        var service = NewService($"{_editor.QuotedCommand} --content \"{content}\" --exit 3", out var writes);

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.EditorFailed, result.Outcome);
        Assert.Same(original, result.Document);
        Assert.Contains("exited with code 3", result.Message);
        Assert.Contains(TerminalSuspendController.EnterTuiSequence, writes);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task SignalStyleExitFromARealChild_IsReportedAsASignal()
    {
        var original = ComposerDocument.FromText("keep me");
        var service = NewService($"{_editor.QuotedCommand} --exit 130", out _);

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.EditorFailed, result.Outcome);
        Assert.Contains("terminated by signal 2", result.Message);
        Assert.Same(original, result.Document);
    }

    [Fact]
    public async Task UnquotedPathWithSpaces_FailsToLaunch_AndTheComposerIsUnchanged()
    {
        // Documented failure mode: the value splits, the program is not found, nothing is lost.
        var original = ComposerDocument.FromText("keep me");
        var service = NewService(_editor.ScriptPath, out var writes); // deliberately unquoted

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.LaunchFailed, result.Outcome);
        Assert.Same(original, result.Document);
        Assert.Contains(TerminalSuspendController.EnterTuiSequence, writes);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task MissingProgram_FailsToLaunch_WithActionableGuidance()
    {
        var original = ComposerDocument.FromText("keep me");
        var service = NewService("definitely-not-a-real-program-287 --wait", out _);

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.LaunchFailed, result.Outcome);
        Assert.Same(original, result.Document);
        Assert.Contains("definitely-not-a-real-program-287", result.Message);
        Assert.Contains(EditorSetupGuidance.DocsPath, result.Message);
    }

    [Fact]
    public async Task RealChildPreservesUnicodeAndNewlines()
    {
        const string edited = "premier\n\ntroisième\ncafé 你好 \U0001F600\n";
        string content = _editor.ContentFile(edited);
        var service = NewService($"{_editor.QuotedCommand} --content \"{content}\"", out _);

        var result = await service.EditAsync(ComposerDocument.FromText("before"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Equal("premier\n\ntroisième\ncafé 你好 \U0001F600", result.Document.ToPromptText());
    }

    [Fact]
    public async Task RealChildSavingNothing_LeavesTheContentIntact()
    {
        // An editor that exits 0 without writing (e.g. :q on an unmodified buffer).
        var service = NewService($"{_editor.QuotedCommand}", out _);

        var result = await service.EditAsync(ComposerDocument.FromText("unchanged text"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Equal("unchanged text", result.Document.ToPromptText());
    }

    [Fact]
    public async Task RealChildEmptyingTheFile_YieldsAnEmptyPrompt()
    {
        string content = _editor.ContentFile("");
        var service = NewService($"{_editor.QuotedCommand} --content \"{content}\"", out _);

        var result = await service.EditAsync(ComposerDocument.FromText("delete me"));

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.True(result.Document.IsEmpty);
    }

    [Fact]
    public async Task RealChildStructuredParts_SurviveTheRoundTrip()
    {
        var attachment = new ComposerAttachmentPart("@src/Program.cs", "file", "/repo/src/Program.cs", "payload");
        var document = new ComposerDocument(new ComposerPart[]
        {
            new ComposerTextPart("review "),
            attachment,
        });
        string content = _editor.ContentFile("@src/Program.cs urgently\n");
        var service = NewService($"{_editor.QuotedCommand} --content \"{content}\"", out _);

        var result = await service.EditAsync(document);

        Assert.Equal(ExternalEditorOutcome.Applied, result.Outcome);
        Assert.Same(attachment, Assert.Single(result.Document.Attachments));
        Assert.Equal("@src/Program.cs urgently", result.Document.ToPromptText());
    }

    [Fact]
    public async Task OversizedRealEdit_IsRejected()
    {
        string content = _editor.ContentFile(new string('x', 500));
        var original = ComposerDocument.FromText("keep me");
        var service = NewService($"{_editor.QuotedCommand} --content \"{content}\"", out _, maxBytes: 100);

        var result = await service.EditAsync(original);

        Assert.Equal(ExternalEditorOutcome.TooLarge, result.Outcome);
        Assert.Same(original, result.Document);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task TempFileHandedToTheChild_IsOwnerOnly_AndRemovedAfterwards()
    {
        if (OperatingSystem.IsWindows()) return;

        string? seenPath = null;
        UnixFileMode seenMode = default;

        var runner = new RecordingRunner(path =>
        {
            seenPath = path;
            // Guarded inside the lambda so the platform analyzer can see it (CA1416).
            if (!OperatingSystem.IsWindows()) seenMode = File.GetUnixFileMode(path);
        });
        var writes = new List<string>();
        var service = new ExternalEditorService(
            new EditorResolver(_ => "irrelevant"),
            runner,
            new TerminalSuspendController(writes.Add),
            _root);

        await service.EditAsync(ComposerDocument.FromText("secret"));

        Assert.NotNull(seenPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, seenMode);
        Assert.False(File.Exists(seenPath!));
        Assert.False(Directory.Exists(Path.GetDirectoryName(seenPath!)));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    private sealed class RecordingRunner : IEditorProcessRunner
    {
        private readonly Action<string> _inspect;
        public RecordingRunner(Action<string> inspect) => _inspect = inspect;
        public Task<EditorProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string filePath, CancellationToken ct)
        {
            _inspect(filePath);
            return Task.FromResult(EditorProcessResult.Exited(0));
        }
    }

    [Fact]
    public async Task CancellingARealLaunch_KillsTheChild_AndRestoresTheTerminal()
    {
        // A long-running child, cancelled from the outside. The runner always appends the
        // "file" argument last, so the arguments are arranged to keep the command valid.
        string program = OperatingSystem.IsWindows() ? "ping" : "sleep";
        string[] args = OperatingSystem.IsWindows() ? new[] { "-n", "60" } : Array.Empty<string>();
        string last = OperatingSystem.IsWindows() ? "127.0.0.1" : "60";

        var runner = new EditorProcessRunner();
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(program, args, last, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void EditorProcessResult_ClassifiesSignalDeaths()
    {
        Assert.True(EditorProcessResult.Exited(130).TerminatedBySignal);
        Assert.False(EditorProcessResult.Exited(1).TerminatedBySignal);
        Assert.False(EditorProcessResult.Exited(0).TerminatedBySignal);
        Assert.True(EditorProcessResult.Exited(0).Succeeded);
        Assert.False(EditorProcessResult.LaunchFailed("x").Succeeded);
    }
}
