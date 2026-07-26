using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Modes;

namespace Andy.Cli.Commands;

/// <summary>
/// <c>/mode</c> - shows or switches the session's primary operating mode (issue #278).
///
/// The command is the ONLY interactive path out of Plan mode, which is what makes
/// <see cref="ModeChangeSource.UserCommand"/> the sole source allowed to re-enable mutation.
/// Unknown mode names are rejected rather than defaulted, matching the fail-closed parsing in
/// <see cref="AgentModeCatalog.TryParse"/>.
/// </summary>
public sealed class ModeCommand : ICommand
{
    private readonly AgentModeState _state;

    public ModeCommand(AgentModeState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public string Name => "mode";

    public string Description => "Show or switch the operating mode (build, plan)";

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

        if (!AgentModeCatalog.TryParse(args[0], out var target) || target is null)
        {
            return CommandResult.Failure(
                $"Unknown mode '{args[0]}'. Known modes: {AgentModeCatalog.KnownIds}.");
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

        return sb.ToString().TrimEnd();
    }
}
