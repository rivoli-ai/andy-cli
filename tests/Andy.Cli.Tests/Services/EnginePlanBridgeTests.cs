using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public sealed class EnginePlanBridgeTests
{
    [Fact]
    public void TryConnect_EnablesPlanningAndPublishesCurrentAndChangedPlans()
    {
        var agent = new FakePlanningAgent
        {
            CurrentPlan = Plan(1, ("inspect", "Inspect files", FakePlanStatus.Pending)),
        };
        var published = new List<AgentPlanView?>();

        using var connection = EnginePlanBridge.TryConnect(agent, published.Add);

        Assert.NotNull(connection);
        Assert.True(agent.PlanningEnabled);
        var initial = Assert.Single(published)!;
        Assert.Equal(1, initial.Revision);
        Assert.Equal("Inspect files", Assert.Single(initial.Items).Text);

        agent.Publish(Plan(
            2,
            ("inspect", "Inspect files", FakePlanStatus.Completed),
            ("change", "Implement change", FakePlanStatus.InProgress)));

        var update = Assert.IsType<AgentPlanView>(published[^1]);
        Assert.Equal(2, update.Revision);
        Assert.Equal(AgentPlanItemViewStatus.Completed, update.Items[0].Status);
        Assert.Equal(AgentPlanItemViewStatus.InProgress, update.Items[1].Status);
    }

    [Fact]
    public void Connection_DisposeStopsPublishing()
    {
        var agent = new FakePlanningAgent { CurrentPlan = Plan(1) };
        var published = new List<AgentPlanView?>();
        var connection = EnginePlanBridge.TryConnect(agent, published.Add)!;

        connection.Dispose();
        agent.Publish(Plan(2, ("later", "Later", FakePlanStatus.Pending)));

        Assert.Single(published);
    }

    [Fact]
    public void TryConnect_ReturnsNullWhenEngineDoesNotExposePlanningApi()
    {
        var published = new List<AgentPlanView?>();

        var connection = EnginePlanBridge.TryConnect(new object(), published.Add);

        Assert.Null(connection);
        Assert.Empty(published);
    }

    [Fact]
    public void Refresh_PublishesRestoredPlanAndNullAfterClear()
    {
        var agent = new FakePlanningAgent { CurrentPlan = Plan(1) };
        var published = new List<AgentPlanView?>();
        using var connection = EnginePlanBridge.TryConnect(agent, published.Add)!;

        agent.CurrentPlan = Plan(
            5,
            ("done", "Restored task", FakePlanStatus.Completed));
        connection.Refresh();
        agent.CurrentPlan = null;
        connection.Refresh();

        Assert.Equal(3, published.Count);
        Assert.Equal(5, published[1]!.Revision);
        Assert.Null(published[2]);
    }

    private static FakePlanSnapshot Plan(
        int revision,
        params (string Id, string Text, FakePlanStatus Status)[] items) =>
        new()
        {
            Revision = revision,
            Items = items
                .Select(item => new FakePlanItem
                {
                    Id = item.Id,
                    Text = item.Text,
                    Status = item.Status,
                })
                .ToArray(),
        };

    private sealed class FakePlanningAgent
    {
        public bool PlanningEnabled { get; private set; }
        public FakePlanSnapshot? CurrentPlan { get; set; }
        public event EventHandler<FakePlanChangedEventArgs>? PlanChanged;

        public void EnablePlanning() => PlanningEnabled = true;

        public void Publish(FakePlanSnapshot plan)
        {
            CurrentPlan = plan;
            PlanChanged?.Invoke(this, new FakePlanChangedEventArgs { Plan = plan });
        }
    }

    private sealed class FakePlanChangedEventArgs : EventArgs
    {
        public required FakePlanSnapshot Plan { get; init; }
    }

    private sealed class FakePlanSnapshot
    {
        public int Revision { get; init; }
        public IReadOnlyList<FakePlanItem> Items { get; init; } = Array.Empty<FakePlanItem>();
    }

    private sealed class FakePlanItem
    {
        public required string Id { get; init; }
        public required string Text { get; init; }
        public FakePlanStatus Status { get; init; }
    }

    private enum FakePlanStatus
    {
        Pending,
        InProgress,
        Completed,
    }
}
