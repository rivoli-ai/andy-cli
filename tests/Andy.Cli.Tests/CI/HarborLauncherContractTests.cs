using System.IO;
using Xunit;

namespace Andy.Cli.Tests.CI;

public sealed class HarborLauncherContractTests
{
    [Fact]
    public void TerminalBenchLauncherPinsOuterAndNestedAgentDeadlines()
    {
        var script = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "scripts", "harbor", "run-terminal-bench.sh"));

        Assert.Contains(
            "HARBOR_AGENT_TIMEOUT_MULTIPLIER:-12",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "HARBOR_EFFECTIVE_AGENT_TIMEOUT_SECONDS:-3600",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ANDY_CLI_TIMEOUT_SECONDS:-3300",
            script,
            StringComparison.Ordinal);
        Assert.Contains("--agent-timeout-multiplier", script, StringComparison.Ordinal);
        Assert.Contains(
            "--agent-kwarg \"harbor_timeout_seconds=$effective_agent_timeout_seconds\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "--agent-kwarg \"timeout_seconds=$cli_timeout_seconds\"",
            script,
            StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Andy.Cli.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
