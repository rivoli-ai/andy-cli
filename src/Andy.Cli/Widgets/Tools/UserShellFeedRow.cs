using System;
using System.Collections.Generic;
using System.Threading;
using Andy.Cli.Services.Shell;
using Andy.Cli.Services.ToolResults;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Builds and updates the feed row for a user-invoked shell command (issue #286).
    ///
    /// A shell-mode command reuses <see cref="ToolCallItem"/> - the item that owns every tool row's
    /// chrome (status glyph, spinner, elapsed clock, block gutter) - rather than introducing a
    /// second drawing implementation. What makes it a USER row is the presenter
    /// (<see cref="UserShellPresenter"/>) and the marker on the snapshot, not a different widget.
    ///
    /// The snapshot's <c>ToolName</c> is genuinely <c>execute_command</c>, because that is the tool
    /// being invoked and the name the permission gate raises its prompt against. That is what lets
    /// <see cref="FeedView.MarkAwaitingApproval"/> light up this row when the gate asks the user to
    /// consent, with no special-casing in the feed. Attribution lives in the presenter, in the
    /// <see cref="UserShellPresenter.UserInvokedParameterKey"/> marker, and in the separate
    /// session log - never in the tool name.
    /// </summary>
    public static class UserShellFeedRow
    {
        private static int s_sequence;

        /// <summary>
        /// Prefix of the UI row id. Distinct from the ids <c>UiUpdatingToolExecutor</c> mints for
        /// model calls, so the two can never claim or complete each other's rows.
        /// </summary>
        public const string RowIdPrefix = "user_shell_";

        /// <summary>A fresh, process-unique row id.</summary>
        public static string NextRowId() =>
            RowIdPrefix + Interlocked.Increment(ref s_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// The row for a command that has just been submitted and is still running. Shows the
        /// command and the spinner immediately, so a slow command does not look like a dead prompt.
        /// </summary>
        public static ToolCallItem CreateRunning(string command, string workingDirectory, string? rowId = null)
        {
            var snapshot = new ToolCallSnapshot
            {
                ToolId = rowId ?? NextRowId(),
                ToolName = UserShellCommandRunner.ToolId,
                Parameters = new Dictionary<string, object?>
                {
                    ["command"] = command,
                    ["working_directory"] = workingDirectory,
                    [UserShellPresenter.UserInvokedParameterKey] = true,
                },
                IsComplete = false,
                StartedAtUtc = DateTime.UtcNow,
            };
            return new ToolCallItem(snapshot, new UserShellPresenter());
        }

        /// <summary>
        /// Folds a finished command into an existing row. The result object itself becomes the
        /// snapshot's payload, so the presenter reads typed fields instead of re-deriving exit
        /// codes and streams from rendered text.
        /// </summary>
        public static void Complete(ToolCallItem item, UserShellCommandResult result)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(result);

            item.Update(snapshot => snapshot with
            {
                IsComplete = true,
                IsAwaitingApproval = false,
                IsSuccessful = result.Outcome == UserShellOutcome.Succeeded,
                WasCancelled = result.Outcome == UserShellOutcome.Cancelled,
                WasDenied = result.Outcome == UserShellOutcome.Denied,
                Data = result,
                Duration = result.Duration,
                ErrorMessage = result.ErrorMessage,
            });
        }
    }
}
