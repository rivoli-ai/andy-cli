using System.Collections;
using System.Reflection;

namespace Andy.Cli.Services;

internal sealed record AgentPlanItemView(
    string Id,
    string Text,
    AgentPlanItemViewStatus Status);

internal sealed record AgentPlanView(
    int Revision,
    IReadOnlyList<AgentPlanItemView> Items);

internal enum AgentPlanItemViewStatus
{
    Pending,
    InProgress,
    Completed,
}

/// <summary>
/// Connects the CLI to the optional structured-planning API in Andy.Engine.
///
/// Reflection keeps this CLI compatible with the currently published engine package. Once an
/// engine version containing EnablePlanning, PlanChanged, and CurrentPlan is installed, planning
/// turns on automatically. Older engine versions continue to work without exposing a plan.
/// </summary>
internal static class EnginePlanBridge
{
    public static EnginePlanConnection? TryConnect(
        object agent,
        Action<AgentPlanView?> publish)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(publish);

        var agentType = agent.GetType();
        var enablePlanning = agentType.GetMethod(
            "EnablePlanning",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        var planChanged = agentType.GetEvent(
            "PlanChanged",
            BindingFlags.Instance | BindingFlags.Public);
        var eventHandlerType = planChanged?.EventHandlerType;
        var eventArguments = eventHandlerType?.GetGenericArguments();

        if (enablePlanning == null ||
            planChanged == null ||
            eventHandlerType == null ||
            eventArguments is not { Length: 1 } ||
            !typeof(EventArgs).IsAssignableFrom(eventArguments[0]))
        {
            return null;
        }

        enablePlanning.Invoke(agent, null);

        var subscribe = typeof(EnginePlanBridge)
            .GetMethod(nameof(Subscribe), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(eventArguments[0]);
        return (EnginePlanConnection)subscribe.Invoke(
            null,
            new object[] { agent, planChanged, publish })!;
    }

    private static EnginePlanConnection Subscribe<TEventArgs>(
        object agent,
        EventInfo planChanged,
        Action<AgentPlanView?> publish)
        where TEventArgs : EventArgs
    {
        EventHandler<TEventArgs> handler = (_, args) =>
        {
            var plan = args.GetType()
                .GetProperty("Plan", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(args);
            publish(ParsePlan(plan));
        };

        planChanged.AddEventHandler(agent, handler);
        var connection = new EnginePlanConnection(agent, planChanged, handler, publish);
        connection.Refresh();
        return connection;
    }

    internal static AgentPlanView? ReadCurrentPlan(object agent)
    {
        var plan = agent.GetType()
            .GetProperty("CurrentPlan", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(agent);
        return ParsePlan(plan);
    }

    private static AgentPlanView? ParsePlan(object? plan)
    {
        if (plan == null)
            return null;

        var planType = plan.GetType();
        var revisionValue = planType
            .GetProperty("Revision", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(plan);
        var itemsValue = planType
            .GetProperty("Items", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(plan) as IEnumerable;

        if (revisionValue is not int revision || itemsValue == null)
            return null;

        var items = new List<AgentPlanItemView>();
        foreach (var item in itemsValue)
        {
            if (item == null)
                return null;

            var itemType = item.GetType();
            var id = itemType.GetProperty("Id")?.GetValue(item) as string;
            var text = itemType.GetProperty("Text")?.GetValue(item) as string;
            var statusText = itemType.GetProperty("Status")?.GetValue(item)?.ToString();
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(text) ||
                !TryParseStatus(statusText, out var status))
            {
                return null;
            }

            items.Add(new AgentPlanItemView(id, text, status));
        }

        return new AgentPlanView(revision, items);
    }

    private static bool TryParseStatus(
        string? value,
        out AgentPlanItemViewStatus status)
    {
        var normalized = value?
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        status = normalized switch
        {
            "pending" => AgentPlanItemViewStatus.Pending,
            "inprogress" => AgentPlanItemViewStatus.InProgress,
            "completed" => AgentPlanItemViewStatus.Completed,
            _ => default,
        };
        return normalized is "pending" or "inprogress" or "completed";
    }
}

internal sealed class EnginePlanConnection : IDisposable
{
    private readonly object _agent;
    private readonly EventInfo _planChanged;
    private readonly Delegate _handler;
    private readonly Action<AgentPlanView?> _publish;
    private bool _disposed;

    internal EnginePlanConnection(
        object agent,
        EventInfo planChanged,
        Delegate handler,
        Action<AgentPlanView?> publish)
    {
        _agent = agent;
        _planChanged = planChanged;
        _handler = handler;
        _publish = publish;
    }

    public void Refresh()
    {
        if (!_disposed)
            _publish(EnginePlanBridge.ReadCurrentPlan(_agent));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _planChanged.RemoveEventHandler(_agent, _handler);
        _disposed = true;
    }
}
