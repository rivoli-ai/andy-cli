using System;
using Andy.Cli;
using Xunit;

namespace Andy.Cli.Tests;

public class VersionInfoTests
{
    [Fact]
    public void PrefersInformationalVersion_OverAssemblyVersion()
    {
        var result = VersionInfo.ResolveDisplayVersion("2026.5.30", new Version(1, 0, 0, 0));
        Assert.Equal("2026.5.30", result);
    }

    [Fact]
    public void StripsBuildMetadataAfterPlus()
    {
        var result = VersionInfo.ResolveDisplayVersion("2026.5.30+abc1234", new Version(1, 0, 0, 0));
        Assert.Equal("2026.5.30", result);
    }

    [Fact]
    public void KeepsPreReleaseSuffix()
    {
        var result = VersionInfo.ResolveDisplayVersion("2026.5.30-rc.7", new Version(1, 0, 0, 0));
        Assert.Equal("2026.5.30-rc.7", result);
    }

    [Fact]
    public void TrimsWhitespace()
    {
        var result = VersionInfo.ResolveDisplayVersion("  2026.5.30  ", null);
        Assert.Equal("2026.5.30", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToAssemblyVersion_WhenInformationalIsMissing(string? informational)
    {
        var result = VersionInfo.ResolveDisplayVersion(informational, new Version(2026, 5, 30, 0));
        Assert.Equal("2026.5.30", result);
    }

    [Fact]
    public void ReturnsEmpty_WhenNoVersionAvailable()
    {
        var result = VersionInfo.ResolveDisplayVersion(null, null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ResolvesRunningAssemblyVersion_NonEmpty()
    {
        var result = VersionInfo.ResolveDisplayVersion();
        Assert.False(string.IsNullOrWhiteSpace(result));
    }
}
