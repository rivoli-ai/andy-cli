using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Lsp;
using Xunit;

namespace Andy.Cli.Tests.Lsp;

/// <summary>
/// End-to-end coverage of changed-file diagnostics (rivoli-ai/andy-cli#282, phase 1), driven
/// against the deterministic in-repo <see cref="FakeLanguageServer"/> over real streams. No test
/// here needs a language server to be installed.
///
/// The acceptance criteria these map to are called out on each test, because most of them are
/// about what must NOT happen: no hang, no crash, no escape from the workspace.
/// </summary>
public sealed class LspDiagnosticsIntegrationTests
{
    [Fact]
    public async Task ReportsErrorsAndWarningsForTheChangedFile()
    {
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        await using var manager = workspace.Manager(definition);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "clean line\nan ERROR here\na WARN there\n");

        var report = await service.ReportAsync(path, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(LspDiagnosticsStatus.Received, report!.Status);
        Assert.Equal(1, report.ErrorCount);
        Assert.Equal(1, report.WarningCount);

        var error = report.Diagnostics.Single(d => d.Severity == LspDiagnosticSeverity.Error);
        Assert.Equal(2, error.Line);       // 1-based; the server reported 0-based line 1
        Assert.Equal(1, error.Column);
        Assert.Equal("E100", error.Code);
        Assert.Equal("fake", error.Source);
    }

    [Fact]
    public async Task DiagnosticsDescribeTheFileAsItExistsOnDisk()
    {
        // Acceptance: "Diagnostics correspond to the final on-disk version of the changed file."
        // The service reads the file back rather than trusting whatever the caller thought it
        // wrote, which is also what makes a post-mutation formatter (#283) safe to insert ahead of
        // it: whatever the formatter leaves on disk is what gets analyzed.
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        await using var manager = workspace.Manager(definition);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");
        var first = await service.ReportAsync(path, CancellationToken.None);
        Assert.Equal(1, first!.ErrorCount);

        // Something else (a formatter, a second edit) rewrites the file before we ask again.
        File.WriteAllText(path, "all better now\n");
        var second = await service.ReportAsync(path, CancellationToken.None);

        Assert.Equal(LspDiagnosticsStatus.Received, second!.Status);
        Assert.Empty(second.Diagnostics);

        var texts = workspace.Transports.Single().Server.ReceivedTexts.ToArray();
        Assert.Equal("all better now\n", texts.Last());
    }

    [Fact]
    public async Task StartsOneServerPerWorkspaceAndReusesIt()
    {
        // Acceptance: "A configured server starts once per compatible workspace and is reused safely."
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        await using var manager = workspace.Manager(definition);
        var service = new LspDiagnosticsService(manager);

        var first = workspace.WriteFile("one.fake", "an ERROR here\n");
        var second = workspace.WriteFile("nested/two.fake", "a WARN there\n");

        await service.ReportAsync(first, CancellationToken.None);
        await service.ReportAsync(second, CancellationToken.None);
        await service.ReportAsync(first, CancellationToken.None);

        Assert.Single(workspace.Transports);
        Assert.Equal(1, workspace.Transports[0].Server.InitializeCount);

        // Reuse means the second file is opened, not re-opened as if it were the first.
        Assert.Equal(2, workspace.Transports[0].Server.DidOpenCount);
        Assert.Equal(1, workspace.Transports[0].Server.DidChangeCount);
    }

    [Fact]
    public async Task ConcurrentMutationsStartExactlyOneServer()
    {
        // Deduplicated startup (phase 2). Ten files changed at once must not spawn ten processes.
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        await using var manager = workspace.Manager(definition);
        var service = new LspDiagnosticsService(manager);

        var paths = Enumerable.Range(0, 10)
            .Select(index => workspace.WriteFile($"file{index}.fake", "an ERROR here\n"))
            .ToArray();

        var reports = await Task.WhenAll(paths.Select(p => service.ReportAsync(p, CancellationToken.None)));

        Assert.All(reports, report => Assert.Equal(LspDiagnosticsStatus.Received, report!.Status));
        Assert.Single(workspace.Transports);
        Assert.Equal(1, workspace.Transports[0].Server.InitializeCount);
    }

    [Fact]
    public async Task NoConfiguredServerForTheExtensionIsSilent()
    {
        using var workspace = new LspTestWorkspace();
        await using var manager = workspace.Manager(LspTestWorkspace.Definition());
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("notes.txt", "an ERROR here\n");

        Assert.Null(await service.ReportAsync(path, CancellationToken.None));
        Assert.Empty(workspace.Transports);
    }

    [Fact]
    public async Task MissingServerBinaryIsReportedAndNeverThrows()
    {
        // Acceptance: "Server absence ... does not hang or crash the agent loop."
        using var workspace = new LspTestWorkspace();
        var definition = new LspServerDefinition
        {
            Id = "missing",
            Command = "andy-definitely-not-installed-language-server",
            Extensions = new[] { ".fake" },
            StartTimeoutMs = 3000,
            DiagnosticsTimeoutMs = 500,
        };
        var configuration = new LspConfigurationLoadResult(
            new[] { definition }, Array.Empty<string>(), Array.Empty<string>());
        await using var manager = new LspServerManager(configuration, workspace.Root);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");
        var report = await service.ReportAsync(path, CancellationToken.None);

        Assert.Equal(LspDiagnosticsStatus.ServerUnavailable, report!.Status);
        Assert.Contains("andy-definitely-not-installed-language-server", report.Detail);
        Assert.Contains("PATH", report.Detail);

        var status = manager.GetStatuses().Single();
        Assert.Equal(LspServerState.Failed, status.State);
    }

    [Fact]
    public async Task MissingServerBinaryIsNotRetriedOnEveryMutation()
    {
        // A misconfigured server must not spawn (or attempt to spawn) a process per file write.
        using var workspace = new LspTestWorkspace();
        var attempts = 0;
        var definition = LspTestWorkspace.Definition();
        var configuration = new LspConfigurationLoadResult(
            new[] { definition }, Array.Empty<string>(), Array.Empty<string>());
        await using var manager = LspTestWorkspace.ManagerWithTransport(configuration, workspace.Root, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new Andy.Cli.Lsp.Protocol.LspStartupException("fake", "command not found");
        });
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");
        for (var index = 0; index < 5; index++)
        {
            var report = await service.ReportAsync(path, CancellationToken.None);
            Assert.Equal(LspDiagnosticsStatus.ServerUnavailable, report!.Status);
        }

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ServerThatNeverInitializesFailsWithinItsStartTimeout()
    {
        // Acceptance: "startup failure ... does not hang".
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition(startTimeoutMs: 400, diagnosticsTimeoutMs: 400);
        await using var manager = workspace.Manager(definition, FakeServerBehavior.NeverInitialize);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");

        var stopwatch = Stopwatch.StartNew();
        var report = await service.ReportAsync(path, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(LspDiagnosticsStatus.ServerUnavailable, report!.Status);
        Assert.Contains("initialize", report.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task CrashedServerIsReportedAndTheProcessIsNotReusedForever()
    {
        // Acceptance: "crash ... does not hang or crash the agent loop."
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition(diagnosticsTimeoutMs: 800);
        await using var manager = workspace.Manager(definition, FakeServerBehavior.CrashOnFirstSync);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");
        var first = await service.ReportAsync(path, CancellationToken.None);
        Assert.Equal(LspDiagnosticsStatus.ServerUnavailable, first!.Status);

        // A crash earns one bounded automatic restart, and the replacement is a new process.
        var second = await service.ReportAsync(path, CancellationToken.None);
        Assert.NotNull(second);
        Assert.True(workspace.Transports.Count >= 2, "the crashed server should have been replaced once");

        // But not an unbounded supply of them.
        for (var index = 0; index < 5; index++)
        {
            await service.ReportAsync(path, CancellationToken.None);
        }
        Assert.True(
            workspace.Transports.Count <= LspServerManager.MaxAutomaticRestarts + 1,
            $"restart storm: {workspace.Transports.Count} processes");
    }

    [Fact]
    public async Task MalformedMessagesAreToleratedAndDiagnosticsStillArrive()
    {
        // Acceptance: "malformed messages ... do not hang or crash the agent loop."
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        await using var manager = workspace.Manager(definition, FakeServerBehavior.GarbageBeforePublish);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");
        var report = await service.ReportAsync(path, CancellationToken.None);

        Assert.Equal(LspDiagnosticsStatus.Received, report!.Status);
        Assert.Equal(1, report.ErrorCount);
        Assert.True(manager.GetStatuses().Single().MalformedMessageCount > 0, "garbage should have been counted");
    }

    [Fact]
    public async Task SlowServerTimesOutWithinTheConfiguredBudget()
    {
        // Acceptance: "timeout ... does not hang".
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition(diagnosticsTimeoutMs: 250);
        await using var manager = workspace.Manager(definition, FakeServerBehavior.NeverPublish);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");

        var stopwatch = Stopwatch.StartNew();
        var report = await service.ReportAsync(path, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(LspDiagnosticsStatus.TimedOut, report!.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task CancellationReleasesThePendingDiagnosticsRequest()
    {
        // Acceptance: "Cancellation terminates pending requests."
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition(diagnosticsTimeoutMs: 30_000);
        await using var manager = workspace.Manager(definition, FakeServerBehavior.NeverPublish);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        var stopwatch = Stopwatch.StartNew();
        var report = await service.ReportAsync(path, cancellation.Token);
        stopwatch.Stop();

        Assert.NotNull(report);
        Assert.NotEqual(LspDiagnosticsStatus.Received, report!.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task DiagnosticsAreBoundedPerFileWithTruncationMetadata()
    {
        // Acceptance: "Diagnostic results are structured, bounded".
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        await using var manager = workspace.Manager(definition);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "FLOOD\n");
        var report = await service.ReportAsync(path, CancellationToken.None);

        Assert.Equal(LspDiagnosticsStatus.Received, report!.Status);
        Assert.True(report.Diagnostics.Count <= LspLimits.MaxDiagnosticsPerFile);
        Assert.True(report.IsTruncated);
        Assert.Equal(60, report.TotalCount);
        Assert.Equal(60 - report.Diagnostics.Count, report.OmittedCount);
        Assert.False(string.IsNullOrWhiteSpace(report.TruncationReason));

        var payload = report.ToStructuredPayload();
        Assert.True((bool)payload["truncated"]!);
        Assert.Equal(60, (int)payload["total_count"]!);
        Assert.Equal(report.OmittedCount, (int)payload["omitted_count"]!);
        Assert.Contains("more not shown", report.ToFeedText());
    }

    [Fact]
    public async Task FilesOutsideTheWorkspaceAreNeverForwardedToAServer()
    {
        // Acceptance: "Paths and server roots cannot escape the active workspace without an
        // explicit permission."
        using var workspace = new LspTestWorkspace();
        using var elsewhere = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        await using var manager = workspace.Manager(definition);
        var service = new LspDiagnosticsService(manager);

        var outside = elsewhere.WriteFile("outside.fake", "an ERROR here\n");
        var report = await service.ReportAsync(outside, CancellationToken.None);

        Assert.Equal(LspDiagnosticsStatus.OutsideWorkspace, report!.Status);
        Assert.Empty(workspace.Transports);
    }

    [Fact]
    public async Task ExplicitOptInAllowsFilesOutsideTheWorkspace()
    {
        using var workspace = new LspTestWorkspace();
        using var elsewhere = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        await using var manager = workspace.Manager(definition, allowOutsideWorkspace: true);
        var service = new LspDiagnosticsService(manager);

        var outside = elsewhere.WriteFile("outside.fake", "an ERROR here\n");
        var report = await service.ReportAsync(outside, CancellationToken.None);

        Assert.Equal(LspDiagnosticsStatus.Received, report!.Status);
        Assert.Equal(1, report.ErrorCount);
    }

    [Fact]
    public async Task ADeletedFileIsSkippedRatherThanReported()
    {
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        await using var manager = workspace.Manager(definition);
        var service = new LspDiagnosticsService(manager);

        var path = Path.Combine(workspace.Root, "gone.fake");
        var report = await service.ReportAsync(path, CancellationToken.None);

        Assert.Equal(LspDiagnosticsStatus.Skipped, report!.Status);
        Assert.Empty(workspace.Transports);
    }

    [Fact]
    public async Task DisposingTheManagerShutsDownEveryServer()
    {
        using var workspace = new LspTestWorkspace();
        var definition = LspTestWorkspace.Definition();
        var manager = workspace.Manager(definition);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");
        await service.ReportAsync(path, CancellationToken.None);
        Assert.Single(workspace.Transports);
        Assert.False(workspace.Transports[0].HasExited);

        await manager.DisposeAsync();

        Assert.True(workspace.Transports[0].HasExited);
        Assert.True(workspace.Transports[0].Server.ShutdownCount > 0, "a clean shutdown should be attempted first");
    }
}
