using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Modes;

namespace Andy.Cli.Commands;

/// <summary>
/// <c>/mode</c> - shows or switches the session's primary operating mode, and manages the Plan-mode
/// read-only opt-ins (issue #278).
///
/// The switch verbs are the ONLY interactive path out of Plan mode, which is what makes
/// <see cref="ModeChangeSource.UserCommand"/> the sole source allowed to re-enable mutation.
/// Unknown mode names are rejected rather than defaulted, matching the fail-closed parsing in
/// <see cref="AgentModeCatalog.TryParse"/>.
///
/// The grant verbs (<c>grants</c>, <c>allow</c>, <c>allow-server</c>, <c>revoke</c>) are the
/// non-interactive equivalent of the MCP opt-in offer, so the same decision can be made from the
/// command line or by automation without ever entering the TUI. They only ever add read-only
/// opt-ins; a mutating tool cannot be granted.
/// </summary>
public sealed class ModeCommand : ICommand
{
    private readonly AgentModeState _state;
    private readonly PlanModeGrantStore? _grants;

    public ModeCommand(AgentModeState state, PlanModeGrantStore? grants = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _grants = grants;
    }

    public string Name => "mode";

    public string Description =>
        "Show or switch the operating mode (build, plan) and manage Plan-mode tool opt-ins";

    public string[] Aliases => Array.Empty<string>();

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
        => Task.FromResult(Execute(args));

    public CommandResult Execute(string[] args)
    {
        if (args is null || args.Length == 0 ||
            args[0].Equals("status", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.CreateSuccess(Status());
        }

        var verb = args[0];
        var rest = args.Skip(1).ToArray();

        if (verb.Equals("grants", StringComparison.OrdinalIgnoreCase))
        {
            return Grants();
        }

        if (verb.Equals("allow", StringComparison.OrdinalIgnoreCase))
        {
            return RequireGrants(store => rest.Length == 0
                ? CommandResult.Failure(
                    "Usage: /mode allow <tool-id> [<tool-id> ...] - opt specific tools into Plan mode.")
                : Report(store.GrantTools(rest)));
        }

        if (verb.Equals("allow-server", StringComparison.OrdinalIgnoreCase))
        {
            return RequireGrants(store => rest.Length == 0
                ? CommandResult.Failure(
                    "Usage: /mode allow-server <mcp-server-name> - opt every tool from an MCP server into Plan mode.")
                : Report(store.GrantServer(rest[0])));
        }

        if (verb.Equals("revoke", StringComparison.OrdinalIgnoreCase))
        {
            return RequireGrants(store => rest.Length == 0
                ? CommandResult.Failure(
                    "Usage: /mode revoke <tool-id|server-name> [...] - remove Plan-mode opt-ins.")
                : Report(store.Revoke(rest)));
        }

        if (!AgentModeCatalog.TryParse(verb, out var target) || target is null)
        {
            return CommandResult.Failure(
                $"Unknown mode '{verb}'. Known modes: {AgentModeCatalog.KnownIds}. "
                + "Other verbs: grants, allow, allow-server, revoke.");
        }

        var previous = _state.CurrentDefinition;
        if (!_state.TrySet(target.Mode, ModeChangeSource.UserCommand, out var error))
        {
            return CommandResult.Failure(error ?? $"Could not switch to {target.DisplayName} mode.");
        }

        if (previous.Mode == target.Mode)
        {
            return CommandResult.CreateSuccess($"[mode] Already in {target.DisplayName} mode. {target.Summary}");
        }

        var message = new StringBuilder();
        message.AppendLine($"[mode] Switched from {previous.DisplayName} to {target.DisplayName} mode.");
        message.Append(target.Summary);
        if (!target.AllowsMutation)
        {
            message.AppendLine();
            message.Append(
                "File writes, shell commands, and unclassified tools are now denied before they run. "
                + "Existing allow rules do not apply. Return with '/mode build'.");
        }

        return CommandResult.CreateSuccess(message.ToString());
    }

    /// <summary>The current mode plus the full mode list, used by <c>/mode</c> with no arguments.</summary>
    public string Status()
    {
        var current = _state.CurrentDefinition;
        var sb = new StringBuilder();
        sb.AppendLine($"[mode] Current mode: {current.DisplayName} ({current.Id})");
        sb.AppendLine();
        foreach (var mode in AgentModeCatalog.All)
        {
            var marker = mode.Mode == current.Mode ? "*" : " ";
            sb.AppendLine($" {marker} /mode {mode.Id,-6} {mode.Summary}");
        }

        if (_grants is not null)
        {
            sb.AppendLine();
            sb.AppendLine("   /mode grants                Review the Plan-mode read-only opt-ins");
            sb.AppendLine("   /mode allow <tool-id>       Opt specific tools into Plan mode");
            sb.AppendLine("   /mode allow-server <name>   Opt in every tool from an MCP server");
            sb.AppendLine("   /mode revoke <id|name>      Remove an opt-in");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The review view: which opt-ins are in force and where they are recorded.</summary>
    private CommandResult Grants() => RequireGrants(store =>
    {
        var listing = store.List();
        var sb = new StringBuilder();
        sb.AppendLine("[mode] Plan-mode read-only opt-ins");
        sb.AppendLine();

        if (listing.IsEmpty)
        {
            sb.AppendLine("  (none) - Plan mode denies every tool it cannot verify as read-only,");
            sb.AppendLine("  including all MCP tools. Grant one with:");
            sb.AppendLine("    /mode allow <tool-id>");
            sb.AppendLine("    /mode allow-server <mcp-server-name>");
            return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
        }

        AppendSection(sb, $"project ({store.ProjectConfigPath})", listing.ProjectTools, listing.ProjectServers);
        AppendSection(sb, $"user ({store.UserConfigPath})", listing.UserTools, listing.UserServers);
        sb.AppendLine("Remove one with: /mode revoke <tool-id|server-name>");
        sb.AppendLine("Server-wide grants also cover tools that server exposes later.");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    });

    private static void AppendSection(
        StringBuilder sb,
        string title,
        IReadOnlyList<string> tools,
        IReadOnlyList<string> servers)
    {
        if (tools.Count == 0 && servers.Count == 0)
        {
            return;
        }

        sb.AppendLine($"  {title}:");
        foreach (var server in servers)
        {
            sb.AppendLine($"    server {server}  (every tool matching {McpToolNaming.ServerToolPrefix(server)}*)");
        }

        foreach (var tool in tools)
        {
            sb.AppendLine($"    tool   {tool}");
        }

        sb.AppendLine();
    }

    private static CommandResult Report(PlanModeGrantResult result) =>
        result.Success
            ? CommandResult.CreateSuccess("[mode] " + result.Message)
            : CommandResult.Failure("[mode] " + result.Message);

    private CommandResult RequireGrants(Func<PlanModeGrantStore, CommandResult> action) =>
        _grants is null
            ? CommandResult.Failure("[mode] Plan-mode opt-ins are not available in this context.")
            : action(_grants);
}
