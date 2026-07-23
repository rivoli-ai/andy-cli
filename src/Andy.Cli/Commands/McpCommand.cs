using System.Text;
using Andy.Cli.Mcp;

namespace Andy.Cli.Commands;

/// <summary>
/// Reports configured interactive MCP servers and their live connection state.
/// </summary>
public sealed class McpCommand : ICommand
{
    private readonly InteractiveMcpToolHost _host;

    public McpCommand(InteractiveMcpToolHost host)
    {
        _host = host;
    }

    public string Name => "mcp";
    public string Description => "List MCP servers and connection status";
    public string[] Aliases => Array.Empty<string>();

    public Task<CommandResult> ExecuteAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var subcommand = args.Length == 0 ? "list" : args[0].ToLowerInvariant();
        return Task.FromResult(subcommand switch
        {
            "list" or "ls" => BuildResult(includeDetails: false),
            "status" => BuildResult(includeDetails: true),
            "help" or "?" => CommandResult.CreateSuccess(
                "Usage:\n  /mcp list     List configured MCP servers\n"
                + "  /mcp status   Show connection state and registered tools"),
            _ => CommandResult.Failure(
                $"Unknown MCP subcommand: {subcommand}. Use '/mcp help' for usage."),
        });
    }

    private CommandResult BuildResult(bool includeDetails)
    {
        var result = new StringBuilder();
        result.AppendLine($"MCP Servers ({_host.Statuses.Count} configured):");

        if (_host.Statuses.Count == 0)
        {
            result.AppendLine("  No MCP servers configured.");
            result.AppendLine("  Add .andy/mcp-servers.json or configure Mcp:Servers in appsettings.json.");
        }

        foreach (var status in _host.Statuses.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            result.AppendLine(
                $"  [{StatusLabel(status.State)}] {status.Name} ({status.Transport})"
                + $" - {status.ToolIds.Count} tool(s)");

            if (includeDetails)
            {
                if (!string.IsNullOrWhiteSpace(status.Detail))
                {
                    result.AppendLine($"      {status.Detail}");
                }
                foreach (var toolId in status.ToolIds)
                {
                    result.AppendLine($"      {toolId}");
                }
            }
        }

        if (_host.Configuration.Errors.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("Configuration errors:");
            foreach (var error in _host.Configuration.Errors)
            {
                result.AppendLine($"  - {error}");
            }
        }

        return CommandResult.CreateSuccess(result.ToString().TrimEnd());
    }

    private static string StatusLabel(McpServerConnectionState state) => state switch
    {
        McpServerConnectionState.Connected => "connected",
        McpServerConnectionState.Disabled => "disabled",
        _ => "failed",
    };
}
