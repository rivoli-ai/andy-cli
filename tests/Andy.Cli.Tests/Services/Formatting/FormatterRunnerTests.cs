using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Formatting;
using Xunit;

namespace Andy.Cli.Tests.Services.Formatting;

/// <summary>
/// Covers the behaviour issue #283 calls out explicitly: deterministic ordering, no-op formatting,
/// content-changing formatting, missing binaries, nonzero exits, timeouts, cancellation, a
/// formatter that deletes or escapes the target, permission denial BEFORE the process starts, and
/// bounded, redacted diagnostics.
/// </summary>
public sealed class FormatterRunnerTests : IDisposable
{
    private readonly string _root;

    public FormatterRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "andy-fmt-run-" + Guid.NewGuid().ToString("N"));
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
            // Temp cleanup is best-effort.
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static FormatterDefinition Def(string name, string command, int order = 100, params string[] extensions)
        => new()
        {
            Name = name,
            Command = command,
            Order = order,
            Extensions = extensions.Length == 0 ? new[] { ".cs" } : extensions,
        };

    private static FormatterCatalog Catalog(params FormatterDefinition[] definitions)
        => new(definitions, command => "/usr/bin/" + command);

    private static FormatterRunner Runner(
        FormatterCatalog catalog,
        IFormatterProcessRunner processRunner,
        IFormatterPermissionGate? gate = null)
        => new(catalog, processRunner, gate ?? UngatedFormatterPermission.Instance);

    [Fact]
    public async Task NoMatchingFormatter_StartsNoProcessAtAll()
    {
        var path = WriteFile("a.txt", "hello");
        var process = new FakeFormatterProcessRunner();
        var runner = Runner(Catalog(Def("cs", "csfmt", extensions: ".cs")), process);

        var results = await runner.RunAsync(path, _root, CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(process.Invocations);
    }

    [Fact]
    public async Task MatchingFormattersRunInCatalogOrder()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner();
        var runner = Runner(
            Catalog(Def("second", "b", order: 20), Def("first", "a", order: 10)),
            process);

        var results = await runner.RunAsync(path, _root, CancellationToken.None);

        Assert.Equal(new[] { "first", "second" }, results.Select(r => r.FormatterName));
        Assert.Equal(new[] { "a", "b" }, process.Invocations.Select(i => i.Command));
    }

    [Fact]
    public async Task FormatterThatLeavesTheFileAlone_ReportsNoChange()
    {
        var path = WriteFile("a.cs", "unchanged");
        var runner = Runner(Catalog(Def("cs", "csfmt")), new FakeFormatterProcessRunner());

        var result = Assert.Single(await runner.RunAsync(path, _root, CancellationToken.None));

        Assert.Equal(FormatterOutcome.NoChange, result.Outcome);
        Assert.False(result.IsFailure);
        Assert.Equal("unchanged", File.ReadAllText(path));
    }

    [Fact]
    public async Task FormatterThatRewritesTheFile_ReportsChanged()
    {
        var path = WriteFile("a.cs", "int  x=1;");
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt", _ =>
        {
            File.WriteAllText(path, "int x = 1;\n");
            return FakeFormatterProcessRunner.Success();
        });
        var runner = Runner(Catalog(Def("cs", "csfmt")), process);

        var result = Assert.Single(await runner.RunAsync(path, _root, CancellationToken.None));

        Assert.Equal(FormatterOutcome.Changed, result.Outcome);
        Assert.False(result.IsFailure);
        Assert.Equal("int x = 1;\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task MissingBinary_IsReportedAsCommandNotFound_NotSilentSuccess()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt",
            _ => FakeFormatterProcessRunner.NotStarted("could not start 'csfmt': No such file or directory"));
        var runner = Runner(Catalog(Def("cs", "csfmt")), process);

        var result = Assert.Single(await runner.RunAsync(path, _root, CancellationToken.None));

        Assert.Equal(FormatterOutcome.CommandNotFound, result.Outcome);
        Assert.True(result.IsFailure);
        Assert.Contains("could not start", result.Diagnostics);
    }

    [Fact]
    public async Task NonZeroExit_CarriesTheExitCodeAndStderrBackToTheCaller()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt",
            _ => FakeFormatterProcessRunner.Failure(2, "a.cs(1,1): error: unexpected token"));
        var runner = Runner(Catalog(Def("cs", "csfmt")), process);

        var result = Assert.Single(await runner.RunAsync(path, _root, CancellationToken.None));

        Assert.Equal(FormatterOutcome.NonZeroExit, result.Outcome);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unexpected token", result.Diagnostics);
        Assert.Contains("exited with code 2", result.Describe());
    }

    [Fact]
    public async Task Timeout_IsReportedAsAFailureNamingTheBudget()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt", _ => FakeFormatterProcessRunner.TimedOut());
        var definition = Def("cs", "csfmt") with { TimeoutSeconds = 5 };
        var runner = Runner(Catalog(definition), process);

        var result = Assert.Single(await runner.RunAsync(path, _root, CancellationToken.None));

        Assert.Equal(FormatterOutcome.TimedOut, result.Outcome);
        Assert.True(result.IsFailure);
        Assert.Contains("5s", result.Diagnostics);
    }

    [Fact]
    public async Task Cancellation_StopsBeforeTheNextFormatterAndIsReportedAsAFailure()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner()
            .OnCommand("first", _ => FakeFormatterProcessRunner.Cancelled())
            .OnCommand("second", _ => FakeFormatterProcessRunner.Success());
        var runner = Runner(
            Catalog(Def("first", "first", order: 1), Def("second", "second", order: 2)),
            process);

        var results = await runner.RunAsync(path, _root, CancellationToken.None);

        var only = Assert.Single(results);
        Assert.Equal(FormatterOutcome.Cancelled, only.Outcome);
        Assert.True(only.IsFailure);
        Assert.DoesNotContain(process.Invocations, i => i.Command == "second");
    }

    [Fact]
    public async Task AnAlreadyCancelledToken_NeverStartsAProcess()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner();
        var runner = Runner(Catalog(Def("cs", "csfmt")), process);
        using var source = new CancellationTokenSource();
        source.Cancel();

        var result = Assert.Single(await runner.RunAsync(path, _root, source.Token));

        Assert.Equal(FormatterOutcome.Cancelled, result.Outcome);
        Assert.Empty(process.Invocations);
    }

    [Fact]
    public async Task AFormatterThatDeletesTheTarget_IsReportedAndStopsTheRest()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner()
            .OnCommand("destroyer", _ =>
            {
                File.Delete(path);
                return FakeFormatterProcessRunner.Success();
            })
            .OnCommand("later", _ => FakeFormatterProcessRunner.Success());
        var runner = Runner(
            Catalog(Def("destroyer", "destroyer", order: 1), Def("later", "later", order: 2)),
            process);

        var results = await runner.RunAsync(path, _root, CancellationToken.None);

        var only = Assert.Single(results);
        Assert.Equal(FormatterOutcome.TargetMissing, only.Outcome);
        Assert.True(only.IsFailure);
        Assert.DoesNotContain(process.Invocations, i => i.Command == "later");
    }

    [Fact]
    public async Task AFormatterThatReplacesTheTargetWithALinkElsewhere_IsReportedAsEscaped()
    {
        var path = WriteFile("a.cs", "x");
        var elsewhere = WriteFile("elsewhere.cs", "other");
        var process = new FakeFormatterProcessRunner().OnCommand("escaper", _ =>
        {
            File.Delete(path);
            File.CreateSymbolicLink(path, elsewhere);
            return FakeFormatterProcessRunner.Success();
        });
        var runner = Runner(Catalog(Def("escaper", "escaper")), process);

        var result = Assert.Single(await runner.RunAsync(path, _root, CancellationToken.None));

        Assert.Equal(FormatterOutcome.TargetEscaped, result.Outcome);
        Assert.True(result.IsFailure);
        Assert.True(result.IsFatalToPipeline);
    }

    [Fact]
    public async Task AFormatterThatReplacesTheTargetWithADirectory_IsReportedAsEscaped()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner().OnCommand("escaper", _ =>
        {
            File.Delete(path);
            Directory.CreateDirectory(path);
            return FakeFormatterProcessRunner.Success();
        });
        var runner = Runner(Catalog(Def("escaper", "escaper")), process);

        var result = Assert.Single(await runner.RunAsync(path, _root, CancellationToken.None));

        Assert.Equal(FormatterOutcome.TargetEscaped, result.Outcome);
        Assert.Contains("directory", result.Diagnostics);
    }

    [Fact]
    public async Task PermissionDenial_HappensBeforeTheProcessIsEverStarted()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner();
        var gate = new RecordingFormatterPermissionGate(
            _ => FormatterPermissionVerdict.Deny("denied by rule execute_command(csfmt:*)"));
        var runner = Runner(Catalog(Def("cs", "csfmt")), process, gate);

        var result = Assert.Single(await runner.RunAsync(path, _root, CancellationToken.None));

        Assert.Equal(FormatterOutcome.PermissionDenied, result.Outcome);
        Assert.True(result.IsFailure);
        Assert.Empty(process.Invocations);
        Assert.Single(gate.Requests);
        Assert.Contains("csfmt", gate.Requests[0].CommandLine);
    }

    [Fact]
    public async Task PermissionDenialOfOneFormatter_DoesNotBlockTheOthers()
    {
        var path = WriteFile("a.cs", "x");
        var process = new FakeFormatterProcessRunner();
        var gate = new RecordingFormatterPermissionGate(request =>
            request.FormatterName == "blocked"
                ? FormatterPermissionVerdict.Deny("plan mode: no commands may run")
                : FormatterPermissionVerdict.Allow);
        var runner = Runner(
            Catalog(Def("blocked", "blocked", order: 1), Def("allowed", "allowed", order: 2)),
            process,
            gate);

        var results = await runner.RunAsync(path, _root, CancellationToken.None);

        Assert.Equal(FormatterOutcome.PermissionDenied, results[0].Outcome);
        Assert.Equal(FormatterOutcome.NoChange, results[1].Outcome);
        Assert.Equal(new[] { "allowed" }, process.Invocations.Select(i => i.Command));
    }

    [Fact]
    public async Task TheGateSeesTheFullCommandLineAndTheTargetPath()
    {
        var path = WriteFile("a.cs", "x");
        var gate = new RecordingFormatterPermissionGate(_ => FormatterPermissionVerdict.Allow);
        var definition = Def("cs", "csfmt") with { Arguments = new[] { "--write", "$FILE" } };
        var runner = Runner(Catalog(definition), new FakeFormatterProcessRunner(), gate);

        await runner.RunAsync(path, _root, CancellationToken.None);

        var request = Assert.Single(gate.Requests);
        Assert.Equal(path, request.TargetPath);
        Assert.Contains("csfmt --write", request.CommandLine);
        Assert.Contains(path, request.CommandLine);
    }

    [Fact]
    public async Task OnlyTheMutatedFileIsFormatted_SiblingsAreUntouched()
    {
        var target = WriteFile("target.cs", "target");
        var sibling = WriteFile("sibling.cs", "sibling");
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt", request =>
        {
            // A well-behaved formatter is handed exactly one path.
            Assert.Contains(target, request.Arguments);
            Assert.DoesNotContain(sibling, request.Arguments);
            return FakeFormatterProcessRunner.Success();
        });
        var runner = Runner(Catalog(Def("cs", "csfmt")), process);

        await runner.RunAsync(target, _root, CancellationToken.None);

        Assert.Single(process.Invocations);
        Assert.Equal("sibling", File.ReadAllText(sibling));
    }

    [Fact]
    public async Task FormatterDiagnostics_AreBoundedAndSecretsAreRedacted()
    {
        var path = WriteFile("a.cs", "x");
        var noisy = "api_key=sk-supersecretvalue12345\n" + new string('z', 50_000);
        var process = new FakeFormatterProcessRunner().OnCommand("csfmt",
            _ => FakeFormatterProcessRunner.Failure(1, noisy));
        var runner = Runner(Catalog(Def("cs", "csfmt")), process);

        var result = Assert.Single(await runner.RunAsync(path, _root, CancellationToken.None));

        Assert.DoesNotContain("sk-supersecretvalue12345", result.Diagnostics);
        Assert.Contains("[REDACTED]", result.Diagnostics);
        Assert.True(
            result.Diagnostics.Length <= FormatterDiagnostics.MaxDiagnosticChars + 80,
            $"diagnostics were {result.Diagnostics.Length} chars, expected them to be bounded");
    }

    [Fact]
    public void HasFormattersFor_AnswersWithoutRunningAnything()
    {
        var process = new FakeFormatterProcessRunner();
        var runner = Runner(Catalog(Def("cs", "csfmt", extensions: ".cs")), process);

        Assert.True(runner.HasFormattersFor("/tmp/x.cs"));
        Assert.False(runner.HasFormattersFor("/tmp/x.md"));
        Assert.Empty(process.Invocations);
    }
}
