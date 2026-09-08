using Andy.Cli.Headless.Tools;
using Andy.Cli.HeadlessConfig;
using Andy.Tools.Core;

namespace Andy.Cli.Tests.Headless.Tools;

public class CliJsonPipeTests
{
    private static CliSubprocessTool Tool(string script) => new(new HeadlessTool
    {
        Name = "json.test",
        Transport = "cli",
        InputMode = "json",
        Command = ["/bin/sh", "-c", script]
    });

    [Fact]
    public async Task LargeOutputBeforeReadingLargeInputDoesNotDeadlock()
    {
        if (OperatingSystem.IsWindows()) return;
        var tool = Tool("head -c 131072 /dev/zero; head -c 131072 /dev/zero >&2; cat");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await tool.ExecuteAsync(new() { ["arguments"] = new { marker = "received", data = new string('x', 200000) } },
            new ToolExecutionContext { CancellationToken = timeout.Token });
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Contains("received", result.Data?.ToString());
    }

    [Fact]
    public async Task CancellationWhileChildDoesNotReadStdinPropagates()
    {
        if (OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tool("sleep 30").ExecuteAsync(
            new() { ["arguments"] = new { data = new string('x', 500000) } },
            new ToolExecutionContext { CancellationToken = timeout.Token }));
    }

    [Fact]
    public async Task OversizedPayloadFailsBeforeSpawn()
    {
        var result = await Tool("exit 0").ExecuteAsync(new() { ["arguments"] = new { data = new string('x', 1048576) } }, new());
        Assert.False(result.IsSuccessful);
        Assert.Contains("maximum size", result.ErrorMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(true)]
    [InlineData("text")]
    public void RejectsScalarJsonArguments(object value) =>
        Assert.NotEmpty(Tool("exit 0").ValidateParameters(new() { ["arguments"] = value }));
}
