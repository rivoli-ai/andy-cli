using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Sessions;

namespace Andy.Cli.Commands;

/// <summary>
/// Lists resumable saved sessions (issue #231). Used both by the one-shot
/// "andy-cli sessions" CLI command and the interactive /sessions slash command.
/// </summary>
public class SessionsCommand : ICommand
{
    public const string NoSessionsMessage =
        "No saved sessions. Sessions are saved automatically after each interactive turn.";

    private readonly SessionStore _store;

    public SessionsCommand(SessionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "sessions";

    public string Description => "List saved sessions that can be resumed";

    public string[] Aliases => Array.Empty<string>();

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args is { Length: > 0 } && args[0] != "list" && args[0] != "ls")
        {
            return Task.FromResult(CommandResult.Failure("Usage: sessions (lists saved sessions)"));
        }

        return Task.FromResult(CommandResult.CreateSuccess(FormatList()));
    }

    /// <summary>Renders the session listing (newest first) as plain fixed-width text.</summary>
    public string FormatList()
    {
        var sessions = _store.List();
        if (sessions.Count == 0)
        {
            return NoSessionsMessage;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Saved sessions ({sessions.Count}, newest first):");
        sb.AppendLine();
        foreach (var session in sessions)
        {
            var updated = session.UpdatedUtc == DateTimeOffset.MinValue
                ? "unknown time"
                : session.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var modelInfo = string.IsNullOrEmpty(session.Model)
                ? ""
                : $"  {session.Provider}/{session.Model}";
            sb.AppendLine($"  {session.SessionId}  {updated}  {session.TurnCount} turn{(session.TurnCount == 1 ? "" : "s")}{modelInfo}");
            if (!string.IsNullOrEmpty(session.FirstUserMessage))
            {
                sb.AppendLine($"    > {session.FirstUserMessage}");
            }
        }
        sb.AppendLine();
        sb.Append("Resume with: andy-cli --resume <session-id> (or --continue for the most recent).");
        return sb.ToString();
    }
}
