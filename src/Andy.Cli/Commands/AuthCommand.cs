using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Auth;

namespace Andy.Cli.Commands;

/// <summary>
/// Provider authentication: <c>andy-cli auth list|login|status|logout</c> and the equivalent
/// TUI <c>/auth</c> command.
///
/// The command is a thin argument parser over <see cref="AuthService"/>; both front ends share
/// the service, so their behaviour and their wording cannot drift. The only difference between
/// them is the <see cref="IAuthPrompt"/> supplied: the console prompt masks with
/// <c>Console.ReadKey</c>, the TUI supplies a modal that masks inside the frame.
///
/// SECURITY: no credential value is ever accepted as a command-line argument. Process arguments
/// are visible to other users and are recorded in shell history, so login always reads the
/// secret from an interactive masked prompt or from redirected stdin.
/// </summary>
public sealed class AuthCommand : ICommand
{
    private readonly AuthService _service;
    private readonly Func<IAuthPrompt> _promptFactory;

    public AuthCommand(AuthService? service = null, Func<IAuthPrompt>? promptFactory = null)
    {
        _service = service ?? AuthService.CreateDefault();
        _promptFactory = promptFactory ?? (() => new ConsoleAuthPrompt());
    }

    /// <summary>Convenience constructor for the one-shot command path.</summary>
    public AuthCommand(IServiceProvider serviceProvider)
        : this(service: null, promptFactory: null)
    {
        // The auth stack deliberately has no DI dependencies: it must work identically in the
        // TUI, in headless mode, and before any LLM service graph exists.
        _ = serviceProvider;
    }

    public string Name => "auth";

    public string Description => "Sign in to providers, review credential status, and sign out";

    public string[] Aliases => new[] { "/auth", "login" };

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
        => ExecuteAsync(args, _promptFactory(), cancellationToken);

    /// <summary>
    /// Runs the command with an explicit prompt implementation. The TUI uses this to supply its
    /// in-frame masked modal.
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(
        string[] args,
        IAuthPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(prompt);

        var subcommand = args.Length == 0 ? "list" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            switch (subcommand)
            {
                case "list" or "ls":
                    return CommandResult.CreateSuccess(await _service.ListAsync(cancellationToken).ConfigureAwait(false));

                case "status" or "info":
                    return CommandResult.CreateSuccess(
                        await _service.StatusAsync(rest.FirstOrDefault(), cancellationToken).ConfigureAwait(false));

                case "login" or "signin":
                    return await LoginAsync(rest, prompt, cancellationToken).ConfigureAwait(false);

                case "logout" or "signout":
                    return await LogoutAsync(rest, cancellationToken).ConfigureAwait(false);

                case "help" or "-h" or "--help" or "?":
                    return CommandResult.CreateSuccess(HelpText());

                default:
                    return CommandResult.Failure(
                        $"Unknown auth subcommand: {subcommand}{Environment.NewLine}{Environment.NewLine}{HelpText()}");
            }
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Failure("Cancelled. Nothing was stored.");
        }
    }

    private async Task<CommandResult> LoginAsync(string[] args, IAuthPrompt prompt, CancellationToken cancellationToken)
    {
        string? provider = null;
        string? method = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--method" or "-m")
            {
                if (i + 1 >= args.Length)
                {
                    return CommandResult.Failure("--method needs a value (api-key, oauth, or device-code).");
                }

                method = args[++i];
                continue;
            }

            if (arg.StartsWith("--method=", StringComparison.Ordinal))
            {
                method = arg["--method=".Length..];
                continue;
            }

            if (arg.StartsWith('-'))
            {
                return CommandResult.Failure(
                    $"Unknown option '{arg}'. A credential value can never be passed as an argument; "
                    + "andy-cli prompts for it, or reads it from stdin.");
            }

            provider ??= arg;
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            return CommandResult.Failure(
                "Usage: auth login <provider> [--method api-key|oauth|device-code]"
                + Environment.NewLine
                + "Run 'auth list' to see the providers and the methods each supports.");
        }

        var result = await _service.LoginAsync(provider, method, prompt, cancellationToken).ConfigureAwait(false);
        return result.Success ? CommandResult.CreateSuccess(result.Message) : CommandResult.Failure(result.Message);
    }

    private async Task<CommandResult> LogoutAsync(string[] args, CancellationToken cancellationToken)
    {
        var provider = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(provider))
        {
            return CommandResult.Failure("Usage: auth logout <provider>");
        }

        var result = await _service.LogoutAsync(provider, cancellationToken).ConfigureAwait(false);
        return result.Success ? CommandResult.CreateSuccess(result.Message) : CommandResult.Failure(result.Message);
    }

    /// <summary>The help block, shared by the CLI verb and the TUI command.</summary>
    public static string HelpText() =>
        """
        auth - provider authentication

          auth list                  Providers, credential status, and supported login methods
          auth login <provider>      Sign in (masked prompt; add --method oauth or device-code)
          auth status [provider]     Where each credential comes from, fully redacted
          auth logout <provider>     Remove the stored credential (key and OAuth tokens)

        Credential precedence: environment variables first (never persisted), then the OS
        credential store (macOS Keychain, Windows Credential Manager, Linux Secret Service).
        Automation can pipe a key in: printf '%s' "$KEY" | andy-cli auth login <provider>
        See docs/provider-auth.md for storage, rotation, and recovery.
        """;
}
