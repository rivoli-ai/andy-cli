using System.Text.Json;
using Andy.Tools.Core;

namespace Andy.Cli.Commands;

/// <summary>Simple interactive naming with an optional detailed history view.</summary>
public static class AgentNameCommand
{
    public static string Execute(IAgentIdentity identity, string arguments)
    {
        var value = arguments.Trim();
        if (value == "--history")
            return JsonSerializer.Serialize(identity.GetSnapshot(), new JsonSerializerOptions
            { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var snapshot = value.Length == 0 ? identity.GetSnapshot()
            : identity.SetName(value == "--clear" ? null : value);
        return $"Agent: {snapshot.Name ?? "(unnamed)"} [{snapshot.AgentId}]";
    }
}
