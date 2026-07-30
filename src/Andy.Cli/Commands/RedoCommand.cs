using System;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Undo;

namespace Andy.Cli.Commands;

/// <summary>
/// Reapplies the turn most recently reverted with /undo (issue #276). The redo
/// history is dropped as soon as a new turn starts.
/// </summary>
public class RedoCommand : ICommand
{
    public const string Usage = "Usage: /redo (takes no arguments)";

    private readonly UndoManager _manager;

    public RedoCommand(UndoManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public string Name => "redo";

    public string Description => "Reapply the turn reverted by the last /undo";

    public string[] Aliases => Array.Empty<string>();

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args is { Length: > 0 })
        {
            return Task.FromResult(CommandResult.Failure(Usage));
        }

        try
        {
            var outcome = _manager.Redo();
            return Task.FromResult(outcome.Success
                ? CommandResult.CreateSuccess(outcome.Message)
                : CommandResult.Failure(outcome.Message));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Failure($"Redo failed: {ex.Message}"));
        }
    }
}
