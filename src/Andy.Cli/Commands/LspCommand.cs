using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Lsp;

namespace Andy.Cli.Commands;

/// <summary>
/// Reports and controls the workspace's language servers.
///
/// The status output is deliberately verbose about failure: a language server that will not start
/// is the single most common thing to go wrong here, and the fix is almost always visible in the
/// command line that was tried plus the first lines of the server's own stderr.
/// </summary>
public sealed class LspCommand : ICommand
{
    private readonly Func<LspSession> _session;

    public LspCommand(Func<LspSession>? session = null) => _session = session ?? (() => LspSession.Instance);

    public string Name => "lsp";
    public string Description => "Show language server status and restart servers";
    public string[] Aliases => Array.Empty<string>();

    public async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var subcommand = args.Length == 0 ? "status" : args[0].ToLowerInvariant();

        switch (subcommand)
        {
            case "status":
            case "list":
            case "ls":
                return CommandResult.CreateSuccess(BuildStatus());

            case "restart":
                return await RestartAsync(args.Length > 1 ? args[1] : null, cancellationToken).ConfigureAwait(false);

            case "help":
            case "?":
                return CommandResult.CreateSuccess(
                    "Usage:\n"
                    + "  /lsp status            Show configured language servers and their state\n"
                    + "  /lsp restart [id]      Restart all servers, or just one by id\n"
                    + "\n"
                    + "Servers are configured in .andy/lsp-servers.json (see docs/lsp-diagnostics.md).\n"
                    + "Andy never downloads a language server; the command must already be installed.");

            default:
                return CommandResult.Failure(
                    $"Unknown lsp subcommand: {subcommand}. Use '/lsp help' for usage.");
        }
    }

    private async Task<CommandResult> RestartAsync(string? serverId, CancellationToken cancellationToken)
    {
        var manager = _session().Manager;
        if (manager is null)
        {
            return CommandResult.CreateSuccess("No language servers are configured; nothing to restart.");
        }

        var dropped = await manager.RestartAsync(serverId, cancellationToken).ConfigureAwait(false);
        var scope = serverId is null ? "all language servers" : $"language server '{serverId}'";
        return CommandResult.CreateSuccess(
            dropped == 0
                ? $"Stopped nothing: {scope} had no running process. Remembered failures were cleared; "
                  + "the next matching file change will try again."
                : $"Restarted {scope}: stopped {dropped} process(es). "
                  + "They start again on the next matching file change.");
    }

    private string BuildStatus()
    {
        var session = _session();
        var manager = session.Manager;
        var configuration = session.Configuration;

        var output = new StringBuilder();

        if (manager is null || configuration.Servers.Count == 0)
        {
            output.AppendLine("Language servers (0 configured):");
            output.AppendLine("  No language servers configured.");
            output.AppendLine("  Add .andy/lsp-servers.json or configure Lsp:Servers in appsettings.json.");
            output.AppendLine("  See docs/lsp-diagnostics.md for ready-to-paste examples.");
            AppendErrors(output, configuration);
            return output.ToString().TrimEnd();
        }

        var statuses = manager.GetStatuses();
        output.AppendLine($"Language servers ({configuration.Servers.Count} configured):");
        output.AppendLine($"  workspace: {manager.WorkspaceRoot}");
        if (configuration.AllowOutsideWorkspace)
        {
            output.AppendLine("  workspace containment: DISABLED (allowOutsideWorkspace is set)");
        }
        output.AppendLine();

        foreach (var status in statuses.OrderBy(s => s.ServerId, StringComparer.OrdinalIgnoreCase))
        {
            output.AppendLine($"  [{Label(status.State)}] {status.ServerId}  {status.Command}");
            output.AppendLine($"      extensions: {string.Join(", ", status.Extensions)}");
            if (status.Root is not null)
            {
                output.AppendLine($"      root: {status.Root}");
            }
            if (status.StartedAt is { } started)
            {
                output.AppendLine($"      started: {started.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }
            if (status.RestartCount > 0)
            {
                output.AppendLine($"      automatic restarts: {status.RestartCount}");
            }
            if (status.MalformedMessageCount > 0)
            {
                output.AppendLine($"      malformed messages ignored: {status.MalformedMessageCount}");
            }
            if (!string.IsNullOrWhiteSpace(status.Detail))
            {
                foreach (var line in status.Detail.Split('\n'))
                {
                    output.AppendLine($"      {line.TrimEnd()}");
                }
            }
        }

        AppendErrors(output, configuration);
        return output.ToString().TrimEnd();
    }

    private static void AppendErrors(StringBuilder output, LspConfigurationLoadResult configuration)
    {
        if (configuration.Errors.Count == 0) return;
        output.AppendLine();
        output.AppendLine("Configuration errors:");
        foreach (var error in configuration.Errors)
        {
            output.AppendLine($"  - {error}");
        }
    }

    private static string Label(LspServerState state) => state switch
    {
        LspServerState.Running => "running",
        LspServerState.Starting => "starting",
        LspServerState.NotStarted => "idle",
        LspServerState.Crashed => "crashed",
        LspServerState.Disabled => "disabled",
        _ => "failed",
    };
}
