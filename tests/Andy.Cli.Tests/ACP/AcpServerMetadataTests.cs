using System.Reflection;
using Andy.Cli.ACP;
using Xunit;

namespace Andy.Cli.Tests.ACP;

/// <summary>
/// Tests that ACP server identity is derived from assembly metadata rather
/// than hard-coded strings.
/// </summary>
public class AcpServerMetadataTests
{
    [Fact]
    public void GetName_ReturnsAssemblyName()
    {
        var asm = typeof(AndyAgentProvider).Assembly;
        var expected = asm.GetName().Name;

        Assert.Equal(expected, AcpServerMetadata.GetName(asm));
    }

    [Fact]
    public void GetName_DefaultsToRunningAssembly()
    {
        Assert.False(string.IsNullOrWhiteSpace(AcpServerMetadata.GetName()));
    }

    [Fact]
    public void GetVersion_ReturnsNonEmptyValue()
    {
        var version = AcpServerMetadata.GetVersion(typeof(AndyAgentProvider).Assembly);
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void GetVersion_IsNotHardCodedPlaceholder()
    {
        // The previous implementation hard-coded "1.0.0"; ensure the value now
        // reflects real assembly metadata.
        var asm = typeof(AndyAgentProvider).Assembly;
        var version = AcpServerMetadata.GetVersion(asm);

        var informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var expected = VersionInfo.ResolveDisplayVersion(informational, asm.GetName().Version);

        Assert.Equal(expected, version);
    }
}
