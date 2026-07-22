using System.IO;
using System.Linq;
using Xunit;

namespace Andy.Cli.Tests.CI;

public class ValidationWorkflowTests
{
    [Fact]
    public void EveryReusableValidationRestoreUsesLockedMode()
    {
        var workflowPath = Path.Combine(FindRepoRoot(), ".github", "workflows", "validate.yml");
        var restoreCommands = File.ReadLines(workflowPath)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("run: dotnet restore Andy.Cli.sln", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, restoreCommands.Length);
        Assert.All(restoreCommands, command => Assert.EndsWith(" --locked-mode", command));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Andy.Cli.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
