// Unit tests for CliSubprocessTool (AQ3, rivoli-ai/andy-cli#44).
// Cover: arg-validation rejection, success/failure mapping based on exit
// code, and POSIX/Windows argv passing without shell expansion. The
// subprocess fixtures use sh -c on POSIX and cmd /c on Windows so the
// tests stay portable without depending on a specific binary.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Andy.Cli.Headless.Tools;
using Andy.Cli.HeadlessConfig;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Headless.Tools;

public class CliSubprocessToolTests
{
    [Fact]
    public void ValidateParameters_RejectsNonStringArg()
    {
        var tool = NewTool(EchoCommand());
        var errors = tool.ValidateParameters(new Dictionary<string, object?>
        {
            ["args"] = new object[] { "ok", 42 }
        });
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ValidateParameters_RejectsNulByte()
    {
        var tool = NewTool(EchoCommand());
        var errors = tool.ValidateParameters(new Dictionary<string, object?>
        {
            ["args"] = new[] { "before\0after" }
        });
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulCommand_CapturesStdout()
    {
        var tool = NewTool(EchoCommand());
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["args"] = new[] { "hello-headless" } },
            new ToolExecutionContext { CancellationToken = CancellationToken.None });

        Assert.True(result.IsSuccessful, $"Expected success, got error: {result.ErrorMessage}");
        Assert.Contains("hello-headless", result.Output as string ?? string.Empty);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_ReturnsFailureWithStderr()
    {
        var tool = NewTool(FalseCommand());
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>(),
            new ToolExecutionContext { CancellationToken = CancellationToken.None });

        Assert.False(result.IsSuccessful);
        Assert.Contains("exited", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task ExecuteAsync_ArgsAreVerbatim_NotShellExpanded()
    {
        // Pass an arg containing $HOME — if a shell were involved it would
        // expand; with ProcessStartInfo.ArgumentList it lands as the literal
        // 7-character string and echo prints it back.
        var tool = NewTool(EchoCommand());
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["args"] = new[] { "literal-$HOME" } },
            new ToolExecutionContext { CancellationToken = CancellationToken.None });

        Assert.True(result.IsSuccessful);
        Assert.Contains("literal-$HOME", result.Output as string ?? string.Empty);
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_KillsProcess()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // sleep semantics differ on Windows; this test covers the POSIX
            // path which is also where the run-time container deploys.
            return;
        }

        var tool = NewTool(new HeadlessTool
        {
            Name = "sleeper",
            Transport = "cli",
            Binary = "sh",
            Command = ["sh", "-c", "sleep 30"]
        });

        using var cts = new CancellationTokenSource();
        var task = tool.ExecuteAsync(
            new Dictionary<string, object?>(),
            new ToolExecutionContext { CancellationToken = cts.Token });

        // Give the subprocess a beat to start, then cancel.
        await Task.Delay(150);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    private static CliSubprocessTool NewTool(HeadlessTool config) => new(config);

    private static HeadlessTool EchoCommand() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new HeadlessTool
            {
                Name = "echoer", Transport = "cli", Binary = "cmd",
                Command = ["cmd", "/c", "echo"]
            }
            : new HeadlessTool
            {
                Name = "echoer", Transport = "cli", Binary = "echo",
                Command = ["echo"]
            };

    private static HeadlessTool FalseCommand() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new HeadlessTool
            {
                Name = "falsy", Transport = "cli", Binary = "cmd",
                Command = ["cmd", "/c", "exit 1"]
            }
            : new HeadlessTool
            {
                Name = "falsy", Transport = "cli", Binary = "false",
                Command = ["false"]
            };
}
