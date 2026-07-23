using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Commands;

/// <summary>
/// Restarts the current interactive session without exiting the CLI.
/// Unlike /clear (which clears the transcript and conversation history in
/// place on the existing agent), /restart performs a full session reset:
/// the assistant service is replaced with a fresh instance (new agent,
/// empty history and internal state) and token counters plus prompt
/// history are reset. The actual reset work is supplied by the host loop
/// as a delegate because the session state lives there.
/// </summary>
public class RestartCommand : ICommand
{
    public const string SuccessMessage =
        "**Session restarted.** Conversation context, token counters, and prompt history were reset.";

    private readonly Func<CancellationToken, Task> _restartSession;

    public RestartCommand(Func<CancellationToken, Task> restartSession)
    {
        _restartSession = restartSession ?? throw new ArgumentNullException(nameof(restartSession));
    }

    public string Name => "restart";

    public string Description => "Restart the current session with a fresh conversation context";

    public string[] Aliases => Array.Empty<string>();

    public async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args is { Length: > 0 })
        {
            return CommandResult.Failure("Usage: /restart (takes no arguments)");
        }

        try
        {
            await _restartSession(cancellationToken);
            return CommandResult.CreateSuccess(SuccessMessage);
        }
        catch (Exception ex)
        {
            return CommandResult.Failure($"Failed to restart session: {ex.Message}");
        }
    }
}
