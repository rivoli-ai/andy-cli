using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Services.Formatting;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Services.Formatting;

/// <summary>
/// The pipeline's contract: formatters run first, the diff is computed from the FINAL on-disk
/// bytes, the sibling-feature steps run in their reserved order between the two, and a formatter
/// failure always reaches the agent.
/// </summary>
public sealed class PostMutationPipelineTests : IDisposable
{
    private readonly string _root;

    public PostMutationPipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "andy-fmt-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static FormatterDefinition Def(string name, string command, int order = 100)
        => new() { Name = name, Command = command, Order = order, Extensions = new[] { ".cs" } };

    private PostMutationPipeline Pipeline(
        IFormatterProcessRunner processRunner,
        IEnumerable<IPostMutationStep>? steps = null,
        IFormatterPermissionGate? gate = null,
        params FormatterDefinition[] definitions)
    {
        var catalog = new FormatterCatalog(definitions, command => "/usr/bin/" + command);
        var runner = new FormatterRunner(catalog, processRunner, gate ?? UngatedFormatterPermission.Instance);
        return new PostMutationPipeline(runner, steps);
    }

    private PostMutationRequest Request(string path, string before, bool existed = true)
        => new("write_file", path, Path.GetFileName(path), before, existed, _root);

    [Fact]
    public async Task TheDiffIsComputedFromTheFinalBytes_NotFromWhatTheToolWrote()
    {
        var path = WriteFile("a.cs", "int  x=1;");
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt", _ =>
        {
            File.WriteAllText(path, "int x = 1;");
            return FakeFormatterProcessRunner.Success();
        });
        var pipeline = Pipeline(process, definitions: Def("cs", "csfmt"));

        var result = await pipeline.RunAsync(Request(path, "int y = 0;"), CancellationToken.None);

        Assert.NotNull(result);
        var added = result!.Diff.Lines.Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text).ToArray();

        // The formatted text is what the user sees, and it is exactly what is on disk.
        Assert.Equal(new[] { "int x = 1;" }, added);
        Assert.Equal("int x = 1;", File.ReadAllText(path));
        Assert.DoesNotContain(result.Diff.Lines, l => l.Text == "int  x=1;");
    }

    [Fact]
    public async Task AFormatterThatChangesNothing_LeavesTheDiffAsTheToolWroteIt()
    {
        var path = WriteFile("a.cs", "final");
        var pipeline = Pipeline(new FakeFormatterProcessRunner(), definitions: Def("cs", "csfmt"));

        var result = await pipeline.RunAsync(Request(path, "original"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FormatterOutcome.NoChange, Assert.Single(result!.FormatterResults).Outcome);
        Assert.False(result.FormattingChangedContent);
        Assert.Null(result.AgentReport);
    }

    [Fact]
    public async Task AnUnchangedFileWithNoFormatters_ProducesNothingToShow()
    {
        var path = WriteFile("a.cs", "same");
        var result = await PostMutationPipeline.DiffOnly.RunAsync(
            Request(path, "same"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ACreatedFileCarriesItsContent_AnUpdatedOneDoesNot()
    {
        var created = WriteFile("new.cs", "brand new");
        var createdResult = await PostMutationPipeline.DiffOnly.RunAsync(
            Request(created, string.Empty, existed: false), CancellationToken.None);

        Assert.NotNull(createdResult);
        Assert.Equal(FileChangeKind.Create, createdResult!.Kind);
        Assert.Equal("brand new", createdResult.FinalContent);

        var updated = WriteFile("old.cs", "after");
        var updatedResult = await PostMutationPipeline.DiffOnly.RunAsync(
            Request(updated, "before"), CancellationToken.None);

        Assert.NotNull(updatedResult);
        Assert.Equal(FileChangeKind.Update, updatedResult!.Kind);
        Assert.Null(updatedResult.FinalContent);
    }

    [Fact]
    public async Task AFormatterFailure_AlwaysProducesAnAgentReportWithTheExitCodeAndStderr()
    {
        var path = WriteFile("a.cs", "written");
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt",
            _ => FakeFormatterProcessRunner.Failure(3, "syntax error near line 4"));
        var pipeline = Pipeline(process, definitions: Def("cs", "csfmt"));

        var result = await pipeline.RunAsync(Request(path, "before"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.FormattingFailed);
        var report = result.AgentReport;
        Assert.NotNull(report);
        Assert.Contains("NOT formatter-clean", report);
        Assert.Contains("exited with code 3", report);
        Assert.Contains("syntax error near line 4", report);
    }

    [Fact]
    public async Task AFormatterFailureIsReported_EvenWhenTheFileEndedUpUnreadable()
    {
        var path = WriteFile("a.cs", "written");
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt", _ =>
        {
            File.Delete(path);
            return FakeFormatterProcessRunner.Success();
        });
        var pipeline = Pipeline(process, definitions: Def("cs", "csfmt"));

        var result = await pipeline.RunAsync(Request(path, "before"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.FinalContent);
        Assert.Contains("no longer exists", result.AgentReport);
    }

    [Fact]
    public async Task StepsRunAfterFormattingAndBeforeTheDiff_InOrder()
    {
        var path = WriteFile("a.cs", "written");
        var log = new List<string>();
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt", _ =>
        {
            log.Add("format");
            File.WriteAllText(path, "formatted");
            return FakeFormatterProcessRunner.Success();
        });

        var lsp = new RecordingStep("lsp", PostMutationStepOrder.LspNotify, log);
        var snapshot = new RecordingStep("snapshot", PostMutationStepOrder.SnapshotFinalize, log);

        // Registered out of order on purpose: the pipeline, not the caller, decides the sequence.
        var pipeline = Pipeline(process, new IPostMutationStep[] { lsp, snapshot }, definitions: Def("cs", "csfmt"));

        var result = await pipeline.RunAsync(Request(path, "before"), CancellationToken.None);

        Assert.Equal(new[] { "format", "snapshot", "lsp" }, log);

        // Both sibling steps observe the POST-format bytes, which is the whole point of the ordering.
        Assert.Equal("formatted", snapshot.ObservedContent);
        Assert.Equal("formatted", lsp.ObservedContent);
        Assert.Equal("formatted", result!.FinalContent ?? File.ReadAllText(path));
    }

    [Fact]
    public async Task StepsSeeTheFormatterResults()
    {
        var path = WriteFile("a.cs", "written");
        var log = new List<string>();
        var step = new RecordingStep("snapshot", PostMutationStepOrder.SnapshotFinalize, log);
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt",
            _ => FakeFormatterProcessRunner.Failure(1, "boom"));
        var pipeline = Pipeline(process, new IPostMutationStep[] { step }, definitions: Def("cs", "csfmt"));

        await pipeline.RunAsync(Request(path, "before"), CancellationToken.None);

        Assert.Equal(FormatterOutcome.NonZeroExit, Assert.Single(step.ObservedFormatterResults).Outcome);
    }

    [Fact]
    public async Task AThrowingStep_DoesNotBreakTheDiff()
    {
        var path = WriteFile("a.cs", "after");
        var pipeline = Pipeline(
            new FakeFormatterProcessRunner(),
            new IPostMutationStep[] { new ThrowingStep() });

        var result = await pipeline.RunAsync(Request(path, "before"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Diff.IsEmpty);
    }

    [Fact]
    public async Task PermissionDenial_LeavesTheFileUnformattedAndSaysSo()
    {
        var path = WriteFile("a.cs", "unformatted");
        var process = new FakeFormatterProcessRunner();
        var gate = new RecordingFormatterPermissionGate(
            _ => FormatterPermissionVerdict.Deny("plan mode is active; no commands may run"));
        var pipeline = Pipeline(process, gate: gate, definitions: Def("cs", "csfmt"));

        var result = await pipeline.RunAsync(Request(path, "before"), CancellationToken.None);

        Assert.Empty(process.Invocations);
        Assert.NotNull(result);
        Assert.Contains("plan mode is active", result!.AgentReport);
        Assert.Equal("unformatted", File.ReadAllText(path));
    }

    [Fact]
    public async Task AFileWithNoMatchingFormatter_SkipsFormattingEntirely()
    {
        var path = WriteFile("a.md", "after");
        var process = new FakeFormatterProcessRunner();
        var pipeline = Pipeline(process, definitions: Def("cs", "csfmt"));

        var result = await pipeline.RunAsync(Request(path, "before"), CancellationToken.None);

        Assert.Empty(process.Invocations);
        Assert.NotNull(result);
        Assert.Empty(result!.FormatterResults);
        Assert.Null(result.AgentReport);
    }

    private sealed class RecordingStep : IPostMutationStep
    {
        private readonly List<string> _log;

        public RecordingStep(string name, int order, List<string> log)
        {
            Name = name;
            Order = order;
            _log = log;
        }

        public string Name { get; }
        public int Order { get; }
        public string? ObservedContent { get; private set; }
        public IReadOnlyList<FormatterRunResult> ObservedFormatterResults { get; private set; } =
            Array.Empty<FormatterRunResult>();

        public Task RunAsync(PostMutationContext context, CancellationToken cancellationToken)
        {
            _log.Add(Name);
            ObservedContent = context.FinalContent;
            ObservedFormatterResults = context.FormatterResults;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStep : IPostMutationStep
    {
        public string Name => "throwing";
        public int Order => PostMutationStepOrder.SnapshotFinalize;

        public Task RunAsync(PostMutationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("step blew up");
    }
}
