using Andy.Cli.Modes;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// The shared mode abstraction (issue #278). These lock the wire ids - <c>/mode</c>, session files,
/// the headless <c>--mode</c> flag, and any future ACP session-mode mapping all key off them - and
/// the fail-closed parsing contract.
/// </summary>
public class AgentModeCatalogTests
{
    [Fact]
    public void Build_IsTheDefaultMode()
    {
        Assert.Equal(AgentMode.Build, AgentModeCatalog.DefaultMode);
    }

    [Fact]
    public void WireIds_AreStable()
    {
        Assert.Equal("build", AgentModeCatalog.Build.Id);
        Assert.Equal("plan", AgentModeCatalog.Plan.Id);
    }

    [Fact]
    public void OnlyBuild_AllowsMutation()
    {
        Assert.True(AgentModeCatalog.Build.AllowsMutation);
        Assert.False(AgentModeCatalog.Plan.AllowsMutation);
    }

    [Theory]
    [InlineData("build", AgentMode.Build)]
    [InlineData("BUILD", AgentMode.Build)]
    [InlineData(" plan ", AgentMode.Plan)]
    [InlineData("Plan", AgentMode.Plan)]
    public void TryParse_AcceptsKnownIds_CaseAndWhitespaceInsensitively(string input, AgentMode expected)
    {
        Assert.True(AgentModeCatalog.TryParse(input, out var definition));
        Assert.NotNull(definition);
        Assert.Equal(expected, definition!.Mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("planning")]
    [InlineData("bui1d")]
    [InlineData("yolo")]
    public void TryParse_FailsClosed_OnUnknownValues(string? input)
    {
        // The critical property: an unknown mode never resolves to a definition, so no caller can
        // accidentally treat it as "use the permissive default".
        Assert.False(AgentModeCatalog.TryParse(input, out var definition));
        Assert.Null(definition);
    }

    [Fact]
    public void KnownIds_ListsEveryMode()
    {
        Assert.Contains("build", AgentModeCatalog.KnownIds);
        Assert.Contains("plan", AgentModeCatalog.KnownIds);
        Assert.Equal(2, AgentModeCatalog.All.Count);
    }
}
