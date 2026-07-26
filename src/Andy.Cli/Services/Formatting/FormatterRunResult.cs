using System;

namespace Andy.Cli.Services.Formatting;

/// <summary>What happened when one formatter was applied to one file.</summary>
public enum FormatterOutcome
{
    /// <summary>Ran cleanly and left the file byte-identical.</summary>
    NoChange,

    /// <summary>Ran cleanly and rewrote the file.</summary>
    Changed,

    /// <summary>The permission gate refused before the process was started.</summary>
    PermissionDenied,

    /// <summary>The command could not be launched (binary absent or not executable).</summary>
    CommandNotFound,

    /// <summary>The process exited with a nonzero status.</summary>
    NonZeroExit,

    /// <summary>The process exceeded the formatter's timeout and was killed.</summary>
    TimedOut,

    /// <summary>The caller's cancellation token fired and the process was killed.</summary>
    Cancelled,

    /// <summary>The target file no longer exists after the formatter ran.</summary>
    TargetMissing,

    /// <summary>The target path no longer resolves to the file that was formatted (replaced by a link, moved out).</summary>
    TargetEscaped,
}

/// <summary>
/// One formatter's outcome, including the exit code and the bounded, redacted diagnostics that are
/// returned to the agent on failure.
/// </summary>
/// <param name="FormatterName">The definition's name.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="ExitCode">The process exit code, when a process ran to completion.</param>
/// <param name="Diagnostics">Bounded, redacted stderr (or stdout when stderr was empty).</param>
/// <param name="Duration">How long the attempt took, including the permission check.</param>
public sealed record FormatterRunResult(
    string FormatterName,
    FormatterOutcome Outcome,
    int? ExitCode,
    string Diagnostics,
    TimeSpan Duration)
{
    /// <summary>
    /// True when the formatter did not do its job. Cancellation counts: a cancelled run leaves the
    /// file unformatted, and the agent must not be told otherwise.
    /// </summary>
    public bool IsFailure => Outcome
        is FormatterOutcome.CommandNotFound
        or FormatterOutcome.NonZeroExit
        or FormatterOutcome.TimedOut
        or FormatterOutcome.Cancelled
        or FormatterOutcome.PermissionDenied
        or FormatterOutcome.TargetMissing
        or FormatterOutcome.TargetEscaped;

    /// <summary>
    /// True when this outcome must stop the remaining formatters for this file. Once the target is
    /// gone or has been swapped out, running the next formatter would act on something other than
    /// the file Andy just wrote.
    /// </summary>
    public bool IsFatalToPipeline => Outcome
        is FormatterOutcome.TargetMissing
        or FormatterOutcome.TargetEscaped
        or FormatterOutcome.Cancelled;

    /// <summary>A one-line human/agent-readable explanation, exit code included.</summary>
    public string Describe()
    {
        var head = Outcome switch
        {
            FormatterOutcome.NoChange => "already formatted (no changes)",
            FormatterOutcome.Changed => "reformatted the file",
            FormatterOutcome.PermissionDenied => "permission denied before the process started",
            FormatterOutcome.CommandNotFound => "command not found (Andy never installs formatters)",
            FormatterOutcome.NonZeroExit => $"exited with code {ExitCode?.ToString() ?? "unknown"}",
            FormatterOutcome.TimedOut => "timed out and was killed",
            FormatterOutcome.Cancelled => "cancelled and was killed",
            FormatterOutcome.TargetMissing => "the target file no longer exists after it ran",
            FormatterOutcome.TargetEscaped => "the target path no longer resolves to the file that was written",
            _ => Outcome.ToString(),
        };

        return string.IsNullOrWhiteSpace(Diagnostics) ? head : head + "\n  " + Diagnostics.Replace("\n", "\n  ");
    }
}
