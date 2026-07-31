using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Lsp;
using Andy.Cli.Lsp.Protocol;
using Xunit;

namespace Andy.Cli.Tests.Lsp;

/// <summary>
/// Real-process coverage for the parts of the contract that only a real child process can prove:
/// that a launch failure is an exception we own, and that disposing the manager leaves no orphan.
///
/// These use ordinary system binaries rather than a language server, so nothing has to be
/// installed. The Unix-only tests are skipped elsewhere rather than made brittle.
/// </summary>
public sealed class LspProcessLifecycleTests
{
    private static bool IsUnix => !OperatingSystem.IsWindows();

    [Fact]
    public void LaunchingAMissingBinaryThrowsAnActionableStartupException()
    {
        var definition = new LspServerDefinition
        {
            Id = "missing",
            Command = "andy-no-such-language-server-binary",
            Extensions = new[] { ".fake" },
        };

        var exception = Assert.Throws<LspStartupException>(() =>
            StdioLspTransport.Start(definition, Environment.CurrentDirectory));

        Assert.Equal("missing", exception.ServerId);
        Assert.Contains("andy-no-such-language-server-binary", exception.Message);
        Assert.Contains("PATH", exception.Message);
        Assert.Contains("never downloads", exception.Message);
    }

    [Fact]
    public async Task DisposingTheManagerKillsAServerThatIsStillRunning()
    {
        // Acceptance: "orphan processes are cleaned up". The child here is a long sleep that never
        // answers initialize, which is the worst case: nothing is listening for shutdown/exit, so
        // the only thing that can end it is us killing it.
        if (!IsUnix) return;

        var definition = new LspServerDefinition
        {
            Id = "sleeper",
            Command = "/bin/sh",
            Args = new[] { "-c", "sleep 120" },
            Extensions = new[] { ".fake" },
            StartTimeoutMs = 500,
            DiagnosticsTimeoutMs = 500,
        };

        using var workspace = new LspTestWorkspace();
        StdioLspTransport? captured = null;
        var configuration = new LspConfigurationLoadResult(
            new[] { definition }, Array.Empty<string>(), Array.Empty<string>());
        var manager = new LspServerManager(configuration, workspace.Root, (d, root) =>
        {
            var transport = (StdioLspTransport)StdioLspTransport.Start(d, root);
            captured = transport;
            return transport;
        });

        var service = new LspDiagnosticsService(manager);
        var path = workspace.WriteFile("a.fake", "an ERROR here\n");

        var stopwatch = Stopwatch.StartNew();
        var report = await service.ReportAsync(path, CancellationToken.None);
        stopwatch.Stop();

        // A server that never completes the handshake must fail fast, not hold the tool call open.
        Assert.Equal(LspDiagnosticsStatus.ServerUnavailable, report!.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");

        Assert.NotNull(captured);
        var processId = captured!.ProcessId;
        Assert.True(processId > 0);

        await manager.DisposeAsync();

        // The handshake failure already terminates the transport; disposal must leave nothing.
        Assert.True(await WaitForExitAsync(processId), $"process {processId} outlived the manager");
    }

    [Fact]
    public async Task AServerThatExitsImmediatelyIsReportedAsUnavailable()
    {
        // Acceptance: a server that dies during startup must not hang the caller.
        if (!IsUnix) return;

        var definition = new LspServerDefinition
        {
            Id = "quitter",
            Command = "/bin/sh",
            Args = new[] { "-c", "echo 'boom: missing runtime' 1>&2; exit 3" },
            Extensions = new[] { ".fake" },
            StartTimeoutMs = 4000,
            DiagnosticsTimeoutMs = 500,
        };

        using var workspace = new LspTestWorkspace();
        var configuration = new LspConfigurationLoadResult(
            new[] { definition }, Array.Empty<string>(), Array.Empty<string>());
        await using var manager = new LspServerManager(configuration, workspace.Root);
        var service = new LspDiagnosticsService(manager);

        var path = workspace.WriteFile("a.fake", "an ERROR here\n");

        var stopwatch = Stopwatch.StartNew();
        var report = await service.ReportAsync(path, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(LspDiagnosticsStatus.ServerUnavailable, report!.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");

        // The server's own stderr is the useful part of the report.
        Assert.Contains("boom: missing runtime", report.Detail);
    }

    private static async Task<bool> WaitForExitAsync(int processId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true; // no such process
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
