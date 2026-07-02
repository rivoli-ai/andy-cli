// Unit tests for CliSubprocessTool (AQ3, rivoli-ai/andy-cli#44).
// Cover: arg-validation rejection, success/failure mapping based on exit
// code, and POSIX/Windows argv passing without shell expansion. The
// subprocess fixtures use sh -c on POSIX and cmd /c on Windows so the
// tests stay portable without depending on a specific binary.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Andy.Cli.Headless.Tools;
using Andy.Cli.HeadlessConfig;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Headless.Tools;

public class CliSubprocessToolTests
{
    // ─── Metadata tests ────────────────────────────────────────────────

    [Fact]
    public void Metadata_ArgvMode_DeclaresArgsArrayParameter()
    {
        var tool = NewTool(EchoCommand());
        var param = Assert.Single(tool.Metadata.Parameters);
        Assert.Equal("args", param.Name);
        Assert.Equal("array", param.Type);
    }

    [Fact]
    public void Metadata_JsonMode_DeclaresArgumentsObjectParameter()
    {
        var tool = NewTool(JsonEchoCommand());
        var param = Assert.Single(tool.Metadata.Parameters);
        Assert.Equal("arguments", param.Name);
        Assert.Equal("object", param.Type);
    }

    // ─── Argv mode validation tests ────────────────────────────────────

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
        await Task.Delay(500);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // ─── JSON mode validation tests ──────────────────────────────────────

    [Fact]
    public void JsonMode_ValidateParameters_AcceptsObjectArgument()
    {
        var tool = NewTool(JsonEchoCommand());
        var errors = tool.ValidateParameters(new Dictionary<string, object?>
        {
            ["arguments"] = new Dictionary<string, object?> { ["query"] = "test" }
        });
        Assert.Empty(errors);
    }

    [Fact]
    public void JsonMode_ValidateParameters_RejectsStringArgument()
    {
        var tool = NewTool(JsonEchoCommand());
        var errors = tool.ValidateParameters(new Dictionary<string, object?>
        {
            ["arguments"] = "not an object"
        });
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("not a string"));
    }

    [Fact]
    public void JsonMode_ValidateParameters_RejectsArrayArgument()
    {
        var tool = NewTool(JsonEchoCommand());
        var errors = tool.ValidateParameters(new Dictionary<string, object?>
        {
            ["arguments"] = new object[] { "one", "two" }
        });
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("not an array"));
    }

    [Fact]
    public void JsonMode_ValidateParameters_AcceptsEmptyParameters()
    {
        var tool = NewTool(JsonEchoCommand());
        var errors = tool.ValidateParameters(new Dictionary<string, object?>());
        Assert.Empty(errors);
    }

    // ─── JSON mode execution tests ──────────────────────────────────────

    [Fact]
    public async Task JsonMode_ExecuteAsync_WritesJsonToStdin()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use cat to echo stdin back. The JSON payload goes to stdin,
            // cat writes it to stdout, proving the stdin-pipe works.
            var tool = NewTool(JsonCatCommand());
            var args = new Dictionary<string, object?>
            {
                ["arguments"] = new Dictionary<string, object?> { ["query"] = "hello", ["limit"] = 10 }
            };
            var result = await tool.ExecuteAsync(args,
                new ToolExecutionContext { CancellationToken = CancellationToken.None });

            Assert.True(result.IsSuccessful, $"Expected success, got error: {result.ErrorMessage}");
            var output = result.Output as string ?? string.Empty;
            Assert.Contains("query", output);
            Assert.Contains("hello", output);
        }
    }

    [Fact]
    public async Task JsonMode_ExecuteAsync_EmptyArguments_SendsEmptyJsonObject()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var tool = NewTool(JsonCatCommand());
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?>(),
                new ToolExecutionContext { CancellationToken = CancellationToken.None });

            Assert.True(result.IsSuccessful, $"Expected success, got error: {result.ErrorMessage}");
        }
    }

    [Fact]
    public async Task JsonMode_ExecuteAsync_NonZeroExit_ReturnsFailure()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use a shell command that reads stdin (cat) but exits 1, to verify
            // the runtime reports non-zero exit. We can't use `false` directly
            // because it exits before reading stdin, which can cause a broken-pipe
            // write error depending on OS timing.
            var tool = NewTool(new HeadlessTool
            {
                Name = "json-falsy",
                Transport = "cli",
                Binary = "sh",
                Command = ["sh", "-c", "cat; exit 1"],
                InputMode = "json"
            });
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["arguments"] = new Dictionary<string, object?> { ["x"] = 1 } },
                new ToolExecutionContext { CancellationToken = CancellationToken.None });

            Assert.False(result.IsSuccessful);
            Assert.Contains("exited", result.ErrorMessage ?? string.Empty);
        }
    }

    [Fact]
    public async Task JsonMode_ExecuteAsync_PreservesPropertyNames()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use cat to echo back the JSON. Verify that camelCase property
            // names from the LLM are preserved (not converted to snake_case).
            var tool = NewTool(JsonCatCommand());
            var args = new Dictionary<string, object?>
            {
                ["arguments"] = new Dictionary<string, object?> { ["camelCase"] = "preserved" }
            };
            var result = await tool.ExecuteAsync(args,
                new ToolExecutionContext { CancellationToken = CancellationToken.None });

            Assert.True(result.IsSuccessful, $"Expected success, got error: {result.ErrorMessage}");
            var output = result.Output as string ?? string.Empty;
            Assert.Contains("camelCase", output);
        }
    }

    [Fact]
    public async Task JsonMode_ExecuteAsync_NestedObject_SerializesCorrectly()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var tool = NewTool(JsonCatCommand());
            var args = new Dictionary<string, object?>
            {
                ["arguments"] = new Dictionary<string, object?>
                {
                    ["nested"] = new Dictionary<string, object?> { ["key"] = "value" }
                }
            };
            var result = await tool.ExecuteAsync(args,
                new ToolExecutionContext { CancellationToken = CancellationToken.None });

            Assert.True(result.IsSuccessful, $"Expected success, got error: {result.ErrorMessage}");
            var output = result.Output as string ?? string.Empty;
            Assert.Contains("nested", output);
            Assert.Contains("key", output);
            Assert.Contains("value", output);
        }
    }

    // ─── Backward compatibility: null InputMode defaults to argv ────────

    [Fact]
    public async Task NullInputMode_DefaultsToArgvMode()
    {
        var tool = NewTool(EchoCommand()); // InputMode is null by default
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["args"] = new[] { "compat-test" } },
            new ToolExecutionContext { CancellationToken = CancellationToken.None });

        Assert.True(result.IsSuccessful, $"Expected success, got error: {result.ErrorMessage}");
        Assert.Contains("compat-test", result.Output as string ?? string.Empty);
    }

    // ─── Cancellation in JSON mode ─────────────────────────────────────

    [Fact]
    public async Task JsonMode_Execution_Cancellation_KillsProcess()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // Use a process that reads stdin and then sleeps, so our stdin write
        // completes before we cancel.
        var tool = NewTool(new HeadlessTool
        {
            Name = "json-sleeper",
            Transport = "cli",
            Binary = "sh",
            Command = ["sh", "-c", "cat && sleep 30"],
            InputMode = "json"
        });

        using var cts = new CancellationTokenSource();
        var task = tool.ExecuteAsync(
            new Dictionary<string, object?> { ["arguments"] = new Dictionary<string, object?> { ["x"] = 1 } },
            new ToolExecutionContext { CancellationToken = cts.Token });

        // Give enough time for the JSON to be written, stdin closed, and cat to start sleeping.
        await Task.Delay(500);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

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

    // JSON mode factory helpers

    private static HeadlessTool JsonEchoCommand() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new HeadlessTool
            {
                Name = "json-echoer", Transport = "cli", Binary = "cmd",
                Command = ["cmd", "/c", "echo"],
                InputMode = "json"
            }
            : new HeadlessTool
            {
                Name = "json-echoer", Transport = "cli", Binary = "echo",
                Command = ["echo"],
                InputMode = "json"
            };

    private static HeadlessTool JsonCatCommand() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new HeadlessTool
            {
                Name = "json-catter", Transport = "cli", Binary = "cmd",
                Command = ["cmd", "/c", "type"],
                InputMode = "json"
            }
            : new HeadlessTool
            {
                Name = "json-catter", Transport = "cli", Binary = "cat",
                Command = ["cat"],
                InputMode = "json"
            };

    private static HeadlessTool JsonFalseCommand() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new HeadlessTool
            {
                Name = "json-falsy", Transport = "cli", Binary = "cmd",
                Command = ["cmd", "/c", "exit 1"],
                InputMode = "json"
            }
            : new HeadlessTool
            {
                Name = "json-falsy", Transport = "cli", Binary = "false",
                Command = ["false"],
                InputMode = "json"
            };
}
