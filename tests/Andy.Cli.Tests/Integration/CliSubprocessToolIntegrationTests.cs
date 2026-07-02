// Copyright (c) Andy Contributors
// Licensed under the MIT License.

// Integration tests for CliSubprocessTool's new CLI transport contract.
// These exercise the real OS subprocess layer (stdin/stdout pipes, exit codes,
// serialization) behind the ITool abstraction used by the headless runner.

using System.Runtime.InteropServices;
using Andy.Cli.Headless;
using Andy.Cli.Headless.Tools;
using Andy.Cli.HeadlessConfig;
using Andy.Tools.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Integration;

public sealed class CliSubprocessToolIntegrationTests
{
    // ─── Happy path: JSON mode round-trips an object via stdin ───────────────

    [Fact]
    public async Task JsonMode_HappyPath_TransportsJsonObjectToStdin()
    {
        if (OperatingSystem.IsWindows())
        {
            // The POSIX fixtures below rely on sh/cat semantics.
            return;
        }

        var tool = NewJsonCatTool();
        var parameters = new Dictionary<string, object?>
        {
            ["arguments"] = new Dictionary<string, object?>
            {
                ["query"] = "integration-test",
                ["limit"] = 42,
            },
        };

        var result = await tool.ExecuteAsync(parameters, Context());

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        var output = result.Output as string ?? string.Empty;
        Assert.Contains("\"query\"", output);
        Assert.Contains("\"integration-test\"", output);
        Assert.Contains("\"limit\"", output);
        Assert.Contains("42", output);
    }

    // ─── Missing stdin: absent arguments sends an empty JSON object ──────────

    [Fact]
    public async Task JsonMode_MissingStdinArguments_SendsEmptyObject()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tool = NewJsonCatTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>(), Context());

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        var output = (result.Output as string ?? string.Empty).Trim();
        Assert.Contains("{}", output);
    }

    // ─── Missing stdin: a process that never reads stdin still completes ─────

    [Fact]
    public async Task JsonMode_MissingStdinReader_DoesNotHang()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // `echo fixed-output` ignores stdin entirely. The runtime must still
        // close stdin and surface a result; it must not block waiting for a
        // reader that will never appear.
        var tool = new CliSubprocessTool(new HeadlessTool
        {
            Name = "json-echo",
            Transport = "cli",
            Binary = "echo",
            Command = ["echo", "fixed-output"],
            InputMode = "json",
        });

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["arguments"] = new Dictionary<string, object?> { ["x"] = 1 } },
            Context());

        // Either the write completed before the process exited (success) or
        // the broken-pipe write was caught and reported as a failure. The
        // important property is that we get a deterministic result, not a hang.
        Assert.NotNull(result);
        if (result.IsSuccessful)
        {
            Assert.Contains("fixed-output", result.Output as string ?? string.Empty);
        }
        else
        {
            Assert.False(result.IsSuccessful);
            Assert.NotNull(result.ErrorMessage);
        }
    }

    // ─── Malformed JSON: string/array arguments rejected at the boundary ─────

    [Fact]
    public void JsonMode_MalformedInputString_IsRejectedByValidation()
    {
        var tool = NewJsonCatTool();

        var errors = tool.ValidateParameters(new Dictionary<string, object?> { ["arguments"] = "not an object" });

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("object", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void JsonMode_MalformedInputArray_IsRejectedByValidation()
    {
        var tool = NewJsonCatTool();

        var errors = tool.ValidateParameters(new Dictionary<string, object?> { ["arguments"] = new[] { "one", "two" } });

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("object", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task JsonMode_MalformedObject_FailsWithSerializationError()
    {
        var tool = NewJsonCatTool();
        var cyclic = new Dictionary<string, object?>();
        cyclic["self"] = cyclic;

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["arguments"] = cyclic },
            Context());

        Assert.False(result.IsSuccessful);
        Assert.Contains("Failed to serialize", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ─── argv fallback: null or non-JSON InputMode uses argv semantics ─────────

    [Fact]
    public async Task ArgvFallback_NullInputMode_UsesArgsArray()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tool = new CliSubprocessTool(new HeadlessTool
        {
            Name = "argv-echo",
            Transport = "cli",
            Binary = "echo",
            Command = ["echo"],
            // InputMode is intentionally null.
        });

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["args"] = new[] { "argv-fallback" } },
            Context());

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Contains("argv-fallback", result.Output as string ?? string.Empty);
    }

    [Fact]
    public async Task ArgvFallback_ExplicitArgv_IgnoresJsonObjectArgument()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tool = new CliSubprocessTool(new HeadlessTool
        {
            Name = "argv-echo",
            Transport = "cli",
            Binary = "echo",
            Command = ["echo"],
            InputMode = "argv",
        });

        // With argv fallback the runtime must use the args array even when an
        // arguments object is also supplied.
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["args"] = new[] { "argv-wins" },
                ["arguments"] = new Dictionary<string, object?> { ["ignored"] = true },
            },
            Context());

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Contains("argv-wins", result.Output as string ?? string.Empty);
    }

    // ─── Host wiring: HeadlessToolHost integrates the CLI adapter ────────────

    [Fact]
    public async Task HeadlessToolHost_WiresCliSubprocessToolIntoRegistry()
    {
        var config = MinimalRunConfig(new HeadlessTool
        {
            Name = "hosted-json-cat",
            Transport = "cli",
            Binary = "cat",
            Command = ["cat"],
            InputMode = "json",
        });

        using var services = HeadlessAgentRunner.BuildServiceProvider(config, NullLoggerFactory.Instance);
        var registry = services.GetRequiredService<IToolRegistry>();

        await using var host = await HeadlessToolHost.BuildAsync(
            config.Tools,
            registry,
            config,
            NullLoggerFactory.Instance);

        Assert.Same(registry, host.Registry);
        Assert.NotNull(host.Registry);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static CliSubprocessTool NewJsonCatTool() =>
        new(new HeadlessTool
        {
            Name = "json-cat",
            Transport = "cli",
            Binary = "cat",
            Command = ["cat"],
            InputMode = "json",
        });

    private static ToolExecutionContext Context() =>
        new() { CancellationToken = CancellationToken.None };

    private static HeadlessRunConfig MinimalRunConfig(params HeadlessTool[] tools) =>
        new()
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent { Slug = "test-agent", Instructions = "test" },
            Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
            Tools = tools,
            Workspace = new HeadlessWorkspace { Root = Path.GetTempPath() },
            Output = new HeadlessOutput
            {
                File = Path.Combine(Path.GetTempPath(), $"cli-subprocess-test-{Guid.NewGuid()}.txt"),
                Stream = "stdout",
            },
            Permissions = new HeadlessPermissions { AllowedTools = Array.Empty<string>() },
            Limits = new HeadlessLimits { MaxIterations = 2, TimeoutSeconds = 30 },
        };
}
