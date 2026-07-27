using System.Linq;
using Andy.Permissions.Matching;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Verifies through the packaged Andy.Permissions that "2>&1" no longer becomes a phantom command
/// the user is asked to consent to (rivoli-ai/andy-permissions#12).
/// </summary>
public class RedirectionSplitVerifyTests
{
    [Fact]
    public void RedirectionDoesNotProduceAPhantomCommand()
    {
        var segments = BashCommandSplitter
            .Split("dotnet build src/Andy.Cli/Andy.Cli.csproj --nologo -v q 2>&1 | tail -5")
            .Segments.Select(s => s.Command).ToList();

        Assert.DoesNotContain("1", segments);
        Assert.Contains("dotnet build src/Andy.Cli/Andy.Cli.csproj --nologo -v q 2>&1", segments);
        Assert.Contains("tail -5", segments);
    }

    [Fact]
    public void ABackgroundAmpersandStillSeparatesCommands()
    {
        var segments = BashCommandSplitter.Split("ls & rm -rf /").Segments.Select(s => s.Command).ToList();

        Assert.Contains("ls", segments);
        Assert.Contains("rm -rf /", segments);
    }
}
