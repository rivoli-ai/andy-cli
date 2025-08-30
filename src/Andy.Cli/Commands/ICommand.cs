using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Commands;

public interface ICommand
{
    string Name { get; }
    string Description { get; }
    string[] Aliases { get; }
    Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default);
}

public class CommandResult
{
    public bool Success { get; }
    public string Message { get; }
    
    private CommandResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }
    
    public static CommandResult CreateSuccess(string message = "") => new(true, message);
    public static CommandResult Failure(string message) => new(false, message);
}