using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Formatting;
using Xunit;

namespace Andy.Cli.Tests.Services.Formatting;

/// <summary>
/// The real process runner against real binaries. Everything here is POSIX-shell based, so the
/// tests are skipped on Windows rather than rewritten in a second dialect; the pure logic they
/// guard (bounding, timeout classification) is also covered by the fake-driven tests.
/// </summary>
public class FormatterProcessRunnerTests
{
    private static bool PosixShellAvailable => !OperatingSystem.IsWindows() && File.Exists("/bin/sh");

    private static FormatterProcessRequest Shell(string script, int timeoutSeconds = 30)
        => new("/bin/sh", new[] { "-c", script }, Path.GetTempPath(), TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public async Task ASuccessfulProcess_ReportsExitZeroAndItsOutput()
    {
        if (!PosixShellAvailable) return; // POSIX-only scenario

        var result = await new FormatterProcessRunner()
            .RunAsync(Shell("echo hello; echo oops 1>&2"), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
        Assert.Contains("oops", result.StandardError);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ANonZeroExit_IsReportedWithItsCode()
    {
        if (!PosixShellAvailable) return; // POSIX-only scenario

        var result = await new FormatterProcessRunner()
            .RunAsync(Shell("echo bad 1>&2; exit 3"), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("bad", result.StandardError);
    }

    [Fact]
    public async Task AMissingBinary_IsReportedAsNotStarted_AndNeverThrows()
    {
        if (!PosixShellAvailable) return; // POSIX-only scenario

        var request = new FormatterProcessRequest(
            "/definitely/not/a/real/formatter-" + Guid.NewGuid().ToString("N"),
            Array.Empty<string>(),
            Path.GetTempPath(),
            TimeSpan.FromSeconds(5));

        var result = await new FormatterProcessRunner().RunAsync(request, CancellationToken.None);

        Assert.False(result.Started);
        Assert.NotNull(result.StartFailure);
    }

    [Fact]
    public async Task AProcessThatOverrunsItsTimeout_IsKilledAndReportedAsTimedOut()
    {
        if (!PosixShellAvailable) return; // POSIX-only scenario

        var result = await new FormatterProcessRunner()
            .RunAsync(Shell("sleep 30", timeoutSeconds: 1), CancellationToken.None);

        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task Cancellation_KillsTheProcessAndIsDistinguishedFromATimeout()
    {
        if (!PosixShellAvailable) return; // POSIX-only scenario

        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await new FormatterProcessRunner()
            .RunAsync(Shell("sleep 30", timeoutSeconds: 60), source.Token);

        Assert.True(result.Started);
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task AFloodOfOutput_IsBoundedRatherThanBufferedWhole()
    {
        if (!PosixShellAvailable) return; // POSIX-only scenario

        // ~2 MB of output, far beyond the per-stream cap.
        var result = await new FormatterProcessRunner().RunAsync(
            Shell("i=0; while [ $i -lt 20000 ]; do echo 0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789; i=$((i+1)); done"),
            CancellationToken.None);

        Assert.True(result.Started);
        Assert.True(
            result.StandardOutput.Length <= FormatterProcessRunner.MaxCapturedCharsPerStream + 100,
            $"captured {result.StandardOutput.Length} chars, expected the stream to be bounded");
        Assert.Contains("truncated", result.StandardOutput);
    }
}
