using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class LongOperationRecapTests
{
    [Fact]
    public void ShortTurnWithoutTools_HasNoRecap()
    {
        Assert.Null(SimpleAssistantService.BuildOperationRecap(
            TimeSpan.FromSeconds(4), succeededOperations: 0, failedOperations: 0));
    }

    [Fact]
    public void ShortTurnWithFewTools_HasNoRecap()
    {
        Assert.Null(SimpleAssistantService.BuildOperationRecap(
            TimeSpan.FromSeconds(12), succeededOperations: 3, failedOperations: 1));
    }

    [Fact]
    public void OperationHeavyTurn_HasDeterministicRecap()
    {
        Assert.Equal(
            "Recap: 5 operations in 12.3s (4 succeeded, 1 failed).",
            SimpleAssistantService.BuildOperationRecap(
                TimeSpan.FromSeconds(12.34), succeededOperations: 4, failedOperations: 1));
    }

    [Fact]
    public void LongTurnWithTool_HasRecap()
    {
        Assert.Equal(
            "Recap: 1 operation in 1.0m (1 succeeded, 0 failed).",
            SimpleAssistantService.BuildOperationRecap(
                TimeSpan.FromSeconds(61), succeededOperations: 1, failedOperations: 0));
    }
}
