using System;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Undo;

namespace Andy.Cli.Commands;

/// <summary>
/// Reverts the filesystem changes made by the most recent completed turn
/// (issue #276). The actual work lives in <see cref="UndoManager"/>; this command
/// is the slash-command surface plus the composer restore hook.
/// </summary>
public class UndoCommand : ICommand
{
    public const string Usage = "Usage: /undo (takes no arguments)";

    private readonly UndoManager _manager;
    private readonly Action<string>? _restorePrompt;

    public UndoCommand(UndoManager manager, Action<string>? restorePrompt = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _restorePrompt = restorePrompt;
    }

    public string Name => "undo";

    public string Description => "Revert the file changes made by the last turn";

    public string[] Aliases => Array.Empty<string>();

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args is { Length: > 0 })
        {
            return Task.FromResult(CommandResult.Failure(Usage));
        }

        UndoOutcome outcome;
        try
        {
            outcome = _manager.Undo();
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Failure($"Undo failed: {ex.Message}"));
        }

        if (!outcome.Success)
        {
            return Task.FromResult(CommandResult.Failure(outcome.Message));
        }

        // Put the reverted prompt back in the composer so the user can edit and resend it.
        if (!string.IsNullOrEmpty(outcome.RestoredPrompt))
        {
            try
            {
                _restorePrompt?.Invoke(outcome.RestoredPrompt);
            }
            catch (Exception)
            {
                // Restoring the composer is a convenience; never fail the undo for it.
            }
        }

        return Task.FromResult(CommandResult.CreateSuccess(outcome.Message));
    }
}
