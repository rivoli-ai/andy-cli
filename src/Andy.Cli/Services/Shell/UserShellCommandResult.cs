using System;
using Andy.Cli.Services.Sessions;

namespace Andy.Cli.Services.Shell;

/// <summary>How a user-invoked shell command finished.</summary>
public enum UserShellOutcome
{
    /// <summary>The process ran and exited zero.</summary>
    Succeeded,

    /// <summary>The process ran and exited non-zero, or the tool itself reported a failure.</summary>
    Failed,

    /// <summary>
    /// The permission evaluator refused the command; no process was started. Rendered and recorded
    /// distinctly from a failure because it is a consent outcome the user can revisit.
    /// </summary>
    Denied,

    /// <summary>The user cancelled with Ctrl+C, or the command hit its wall-clock timeout.</summary>
    Cancelled,

    /// <summary>Shell escape is switched off by configuration; nothing was attempted.</summary>
    Disabled
}

/// <summary>
/// The outcome of one command the USER typed in shell mode (issue #286), as opposed to one the
/// model asked for. Everything the feed, the session log and the attach-to-prompt action need is
/// captured here, read from the structured payload <c>execute_command</c> returns rather than
/// scraped from rendered text.
///
/// Output is held VERBATIM. Redaction is applied at the boundaries that leave the user's own
/// terminal - <see cref="Redact"/> is called before anything is written to the session log or
/// attached to a prompt destined for the model - so inspecting your own environment in shell mode
/// still works while nothing secret-shaped reaches disk or the provider. See
/// <c>docs/shell-escape.md</c>.
/// </summary>
/// <param name="Command">The command line exactly as submitted.</param>
/// <param name="Outcome">Terminal state of the invocation.</param>
/// <param name="ExitCode">Process exit code when one was reported.</param>
/// <param name="StandardOutput">Captured stdout, already bounded.</param>
/// <param name="StandardError">Captured stderr, already bounded.</param>
/// <param name="Duration">Measured wall-clock time of the invocation.</param>
/// <param name="WorkingDirectory">Directory the command ran in.</param>
/// <param name="TimedOut">The tool killed the process at its timeout.</param>
/// <param name="StandardOutputTruncated">Characters dropped from the end of stdout, if any.</param>
/// <param name="StandardErrorTruncated">Characters dropped from the end of stderr, if any.</param>
/// <param name="ErrorMessage">The tool's or gate's own message, when it supplied one.</param>
/// <param name="StartedAtUtc">When the invocation began.</param>
public sealed record UserShellCommandResult(
    string Command,
    UserShellOutcome Outcome,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    string WorkingDirectory,
    bool TimedOut,
    int StandardOutputTruncated,
    int StandardErrorTruncated,
    string? ErrorMessage,
    DateTimeOffset StartedAtUtc)
{
    /// <summary>
    /// Marks this as a command the USER ran, never one the model requested. Carried into the
    /// session log and the instrumentation stream so a transcript reader (or an auditor) can tell
    /// the two apart without inferring it from context.
    /// </summary>
    public const string Source = "user";

    /// <summary>True when the command ran to completion with a zero exit code.</summary>
    public bool Succeeded => Outcome == UserShellOutcome.Succeeded;

    /// <summary>True when either stream lost characters to the output cap.</summary>
    public bool WasTruncated => StandardOutputTruncated > 0 || StandardErrorTruncated > 0;

    /// <summary>True when there is nothing at all on either stream.</summary>
    public bool HasNoOutput =>
        string.IsNullOrEmpty(StandardOutput) && string.IsNullOrEmpty(StandardError);

    /// <summary>
    /// A copy with secrets scrubbed from the command line and both streams. Used on every path
    /// that leaves the local terminal: the persisted session log, and the text the user can
    /// explicitly attach to their next prompt.
    /// </summary>
    public UserShellCommandResult Redact(SessionRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        return this with
        {
            Command = redactor.RedactText(Command),
            StandardOutput = string.IsNullOrEmpty(StandardOutput) ? StandardOutput : redactor.RedactText(StandardOutput),
            StandardError = string.IsNullOrEmpty(StandardError) ? StandardError : redactor.RedactText(StandardError),
            ErrorMessage = string.IsNullOrEmpty(ErrorMessage) ? ErrorMessage : redactor.RedactText(ErrorMessage),
        };
    }

    /// <summary>Short status word for the feed trailer and the session log.</summary>
    public string StatusLabel => Outcome switch
    {
        UserShellOutcome.Denied => "denied",
        UserShellOutcome.Cancelled => TimedOut ? "timed out" : "cancelled",
        UserShellOutcome.Disabled => "disabled",
        UserShellOutcome.Failed => ExitCode is { } code ? $"exit {code}" : "failed",
        _ => "exit 0"
    };

    /// <summary>A result for a command that was never attempted because the feature is off.</summary>
    public static UserShellCommandResult DisabledResult(string command, string workingDirectory) => new(
        Command: command,
        Outcome: UserShellOutcome.Disabled,
        ExitCode: null,
        StandardOutput: string.Empty,
        StandardError: string.Empty,
        Duration: TimeSpan.Zero,
        WorkingDirectory: workingDirectory,
        TimedOut: false,
        StandardOutputTruncated: 0,
        StandardErrorTruncated: 0,
        ErrorMessage: "Shell escape is disabled for this session.",
        StartedAtUtc: DateTimeOffset.UtcNow);
}
