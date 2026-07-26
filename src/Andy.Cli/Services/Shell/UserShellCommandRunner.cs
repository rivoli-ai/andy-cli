using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Configuration;
using Andy.Cli.Services.ToolResults;
using Andy.Tools.Core;

namespace Andy.Cli.Services.Shell;

/// <summary>
/// Runs a command the USER typed in shell mode (issue #286).
///
/// SECURITY MODEL - read before changing anything here.
///
/// This class does NOT start a process. It dispatches <c>execute_command</c> through the
/// <see cref="IToolExecutor"/> resolved from DI, which is the permission-DECORATED executor that
/// <c>AddAndyCliPermissions</c> installs (<c>Andy.Permissions.Execution.PermissionedToolExecutor</c>
/// wrapping <c>Andy.Tools.Execution.ToolExecutor</c>). That is the single consent authority for the
/// whole app, so a user-invoked command gets exactly the same treatment as a model-invoked one:
///
/// <list type="bullet">
/// <item><description>layered allow/ask/deny rules (Builtin &lt; User &lt; Project &lt; Local &lt; Injected &lt; Session &lt; Managed);</description></item>
/// <item><description>the built-in deny rules for destructive commands;</description></item>
/// <item><description>the interactive prompt for anything that resolves to Ask, including its
/// dangerous-command risk assessment (<see cref="ApprovalRiskAssessor"/>) and its persisted
/// approval scopes (<see cref="SessionApprovalStore"/>);</description></item>
/// <item><description>any FUTURE gate layered onto the same seam - notably the Plan-mode overlay
/// (issue #278), which installs deny rules for mutating tools. Because shell escape enters through
/// the evaluator rather than around it, Plan mode blocks a user-typed <c>!</c> command with no
/// change to this file.</description></item>
/// </list>
///
/// The one thing that must NEVER be added here is a direct <c>Process.Start</c> or a call into the
/// undecorated <c>Andy.Tools.Execution.ToolExecutor</c>: either would let a keystroke in the
/// composer reach a child process without consent. The capability flags granted below are the same
/// ones <see cref="UiUpdatingToolExecutor.ExecuteAsync"/> grants and are deliberately NOT a consent
/// decision - they only stop the low-level capability check from short-circuiting the tool before
/// the gate has had its say.
///
/// The runner also owns the parts of the contract the tool layer does not: the session's tracked
/// working directory, the wall-clock timeout, the output cap, and turning the tool's structured
/// payload into a <see cref="UserShellCommandResult"/>.
/// </summary>
public sealed class UserShellCommandRunner
{
    /// <summary>The tool id every shell command - user's or model's - is dispatched through.</summary>
    public const string ToolId = "execute_command";

    /// <summary>
    /// Safety-net execution cap. Mirrors <see cref="UiUpdatingToolExecutor"/>: Andy.Tools' executor
    /// cancels every tool at <c>ResourceLimits.MaxExecutionTimeMs</c>, whose default (30s) is
    /// shorter than the command timeout we ask for and would pre-empt it.
    /// </summary>
    private const long ResourceLimitBackstopMs = 30L * 60 * 1000;

    private readonly IToolExecutor _executor;
    private readonly ShellEscapeOptions _options;
    private readonly WorkingDirectoryTracker _workingDirectory;

    public UserShellCommandRunner(
        IToolExecutor executor,
        ShellEscapeOptions? options = null,
        WorkingDirectoryTracker? workingDirectory = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? ShellEscapeOptions.Default;
        _workingDirectory = workingDirectory ?? WorkingDirectoryTracker.Instance;
    }

    /// <summary>Settings this runner was built with.</summary>
    public ShellEscapeOptions Options => _options;

    /// <summary>
    /// Runs <paramref name="command"/> and returns its outcome. Never throws for an ordinary
    /// failure, a denial, a timeout, or a cancellation - each is a distinct
    /// <see cref="UserShellOutcome"/>, because the composer must survive all of them.
    ///
    /// <paramref name="cancellationToken"/> is the Ctrl+C token. It is passed to the tool via
    /// <see cref="ToolExecutionContext.CancellationToken"/>, which is also what the interactive
    /// permission prompt observes: cancelling while a consent prompt is up resolves it to
    /// deny-once rather than leaving the composer wedged.
    /// </summary>
    public async Task<UserShellCommandResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        var directory = _workingDirectory.Current;

        if (!_options.Enabled)
        {
            return UserShellCommandResult.DisabledResult(command ?? string.Empty, directory);
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            return UserShellCommandResult.DisabledResult(command ?? string.Empty, directory) with
            {
                ErrorMessage = "No command was given."
            };
        }

        command = command.Trim();
        var startedAt = DateTimeOffset.UtcNow;

        // The command is handed to the shell as a single argument (`bash -c` / `cmd /c`) by
        // ExecuteCommandTool with no extra wrapping, so quotes, pipes, redirects, globs and
        // non-ASCII text follow the platform shell's own rules - identical to what the model gets.
        // Nothing here re-quotes or re-escapes; doing so would make the approval preview differ
        // from what actually runs.
        var parameters = new Dictionary<string, object?>
        {
            ["command"] = command,
            // Explicit rather than a "cd <dir> &&" preamble, so the string the permission prompt
            // shows the user is exactly the string that executes.
            ["working_directory"] = directory,
            ["timeout_seconds"] = _options.Timeout.TotalSeconds is var s && s > 0 ? (int)s : ShellEscapeOptions.DefaultTimeoutSeconds,
        };

        var context = new ToolExecutionContext
        {
            WorkingDirectory = directory,
            CancellationToken = cancellationToken,
            CorrelationId = "shell-" + Guid.NewGuid().ToString("N")[..8],
        };
        GrantGatedCapabilities(context);
        RaiseResourceLimitBackstop(context, _options.TimeoutSeconds);

        var stopwatch = Stopwatch.StartNew();
        ToolExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(ToolId, parameters, context).ConfigureAwait(false);
            stopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C. The child is killed by the tool layer as the token trips; the TUI keeps
            // running because this is an ordinary result, not an escape out of the loop.
            stopwatch.Stop();
            return Build(command, UserShellOutcome.Cancelled, null, string.Empty, string.Empty,
                stopwatch.Elapsed, directory, timedOut: false, "Interrupted.", startedAt);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Build(command, UserShellOutcome.Failed, null, string.Empty, string.Empty,
                stopwatch.Elapsed, directory, timedOut: false, ex.Message, startedAt);
        }

        // Read the structured payload rather than parsing rendered text; execute_command populates
        // exit_code / stdout / stderr / duration_ms / timed_out / working_directory.
        var data = result.Data;
        var exitCode = ToolData.GetInt(data, "exit_code") ?? ToolData.GetInt(result.Metadata, "exit_code");
        var stdout = ToolData.GetString(data, "stdout", "output") ?? string.Empty;
        var stderr = ToolData.GetString(data, "stderr", "error_output") ?? string.Empty;
        var timedOut = ToolData.GetBool(data, "timed_out") ?? false;
        var reportedDirectory = ToolData.GetString(data, "working_directory") ?? directory;
        var duration = ToolData.GetDuration(data, "duration_ms") ?? stopwatch.Elapsed;

        var outcome = result.IsSuccessful
            ? UserShellOutcome.Succeeded
            : timedOut || result.WasCancelled ? UserShellOutcome.Cancelled
            : UiUpdatingToolExecutor.IsPermissionDenial(result) ? UserShellOutcome.Denied
            : UserShellOutcome.Failed;

        // A cancelled run can also surface as an ordinary failure when the tool reports the kill as
        // a non-zero exit rather than a cancellation, so trust the token over the result shape.
        if (cancellationToken.IsCancellationRequested && outcome == UserShellOutcome.Failed)
        {
            outcome = UserShellOutcome.Cancelled;
        }

        if (outcome == UserShellOutcome.Succeeded)
        {
            // A standalone `cd` persists for the rest of the session, exactly as it does for a
            // model-invoked command, so the header and subsequent tool calls follow the user.
            _workingDirectory.ApplyExecutedCommand(command, directory);
        }

        return Build(command, outcome, exitCode, stdout, stderr, duration, reportedDirectory,
            timedOut, result.IsSuccessful ? null : (result.ErrorMessage ?? result.Message), startedAt);
    }

    private UserShellCommandResult Build(
        string command, UserShellOutcome outcome, int? exitCode, string stdout, string stderr,
        TimeSpan duration, string directory, bool timedOut, string? error, DateTimeOffset startedAt)
    {
        var (boundedOut, droppedOut) = Bound(stdout);
        var (boundedErr, droppedErr) = Bound(stderr);

        return new UserShellCommandResult(
            Command: command,
            Outcome: outcome,
            ExitCode: exitCode,
            StandardOutput: boundedOut,
            StandardError: boundedErr,
            Duration: duration,
            WorkingDirectory: directory,
            TimedOut: timedOut,
            StandardOutputTruncated: droppedOut,
            StandardErrorTruncated: droppedErr,
            ErrorMessage: error,
            StartedAtUtc: startedAt);
    }

    /// <summary>
    /// Caps one stream at the configured budget, keeping the HEAD and reporting how much was
    /// dropped. The head is kept (rather than the tail, as the model-facing formatter does) because
    /// a user watching their own command wants the first error, and the full text is never
    /// reconstructible from the feed anyway - re-run with a pager if you need all of it.
    /// </summary>
    internal (string Text, int Dropped) Bound(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (string.Empty, 0);
        var limit = _options.EffectiveMaxOutputCharacters;
        if (text.Length <= limit) return (text, 0);
        return (text[..limit], text.Length - limit);
    }

    /// <summary>
    /// Grants the capability flags on the execution context. This is NOT consent: the permission
    /// gate downstream is the decision point. Without these flags Andy.Tools' own capability check
    /// rejects <c>execute_command</c> (which declares ProcessExecution) before the gate ever runs,
    /// so a denial would be indistinguishable from a capability error. Same rationale, and same
    /// four flags, as <c>UiUpdatingToolExecutor.GrantGatedCapabilities</c>.
    /// </summary>
    private static void GrantGatedCapabilities(ToolExecutionContext context)
    {
        context.Permissions.FileSystemAccess = true;
        context.Permissions.NetworkAccess = true;
        context.Permissions.ProcessExecution = true;
        context.Permissions.EnvironmentAccess = true;
    }

    /// <summary>
    /// Lifts the framework executor's blanket execution cap above the command timeout we asked
    /// for, so <c>timeout_seconds</c> is what actually governs and a legitimately long command is
    /// not killed at the framework default.
    /// </summary>
    private static void RaiseResourceLimitBackstop(ToolExecutionContext context, int timeoutSeconds)
    {
        if (context.ResourceLimits is null || context.ResourceLimits.MaxExecutionTimeMs <= 0) return;

        var backstop = ResourceLimitBackstopMs;
        if (timeoutSeconds > 0)
        {
            backstop = Math.Max(backstop, (long)timeoutSeconds * 1000 + 5000);
        }
        if (context.ResourceLimits.MaxExecutionTimeMs < backstop)
        {
            context.ResourceLimits.MaxExecutionTimeMs = (int)Math.Min(backstop, int.MaxValue);
        }
    }
}
