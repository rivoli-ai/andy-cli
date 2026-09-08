using Andy.Cli.Services;

namespace Andy.Cli.Tests.Services;

public class ActiveTurnCancellationTests
{
    [Fact]
    public void CancellationDoesNotCarryIntoNextQueuedTurn()
    {
        using var turns = new ActiveTurnCancellation();
        var first = turns.Begin();
        Assert.True(turns.TryCancel());
        Assert.True(first.Token.IsCancellationRequested);
        first.Dispose();
        using var second = turns.Begin();
        first.Dispose();
        Assert.False(second.Token.IsCancellationRequested);
        Assert.True(turns.TryCancel());
        Assert.True(second.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task CompletionRacingEscapeDoesNotThrow()
    {
        using var turns = new ActiveTurnCancellation();
        for (var i = 0; i < 1000; i++)
        {
            var scope = turns.Begin();
            await Task.WhenAll(Task.Run(scope.Dispose), Task.Run(() => turns.TryCancel()));
            Assert.False(turns.TryCancel());
        }
    }
}
