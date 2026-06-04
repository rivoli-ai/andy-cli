using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Permissions.Model;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class CliPermissionPromptTests
{
    private static PermissionRequest Req() =>
        new("execute_command", "Execute Command", "run ls",
            new PermissionEvaluation(PermissionOutcome.Ask, System.Array.Empty<EvaluatedResource>()));

    [Fact]
    public async Task RequestAsync_posts_to_broker_and_completes_with_decision()
    {
        var broker = new PermissionRequestBroker();
        var prompt = new CliPermissionPrompt(broker);

        var task = prompt.RequestAsync(Req(), CancellationToken.None);

        Assert.True(broker.TryDequeue(out var pending));
        Assert.NotNull(pending);
        Assert.False(task.IsCompleted); // still awaiting the UI decision

        pending!.Completion.TrySetResult(new PermissionDecision(true, PersistScope.Session));

        var decision = await task;
        Assert.True(decision.Allowed);
        Assert.Equal(PersistScope.Session, decision.Persist);
        Assert.False(broker.HasPending);
    }

    [Fact]
    public async Task Cancellation_resolves_to_deny()
    {
        var broker = new PermissionRequestBroker();
        var prompt = new CliPermissionPrompt(broker);
        using var cts = new CancellationTokenSource();

        var task = prompt.RequestAsync(Req(), cts.Token);
        cts.Cancel();

        var decision = await task;
        Assert.False(decision.Allowed);
    }
}
