using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// The shell command result, read once from the structured payload Andy.Tools returns.
    /// ExecuteCommandTool populates command / exit_code / stdout / stderr / duration_ms /
    /// timed_out / working_directory; every one of those was previously thrown away except a
    /// first line of "output".
    /// </summary>
    /// <param name="Command">The command line that ran.</param>
    /// <param name="ExitCode">Process exit code, when the tool reported one.</param>
    /// <param name="StandardOutput">Captured stdout.</param>
    /// <param name="StandardError">Captured stderr, kept separate so it can be colored as such.</param>
    /// <param name="Duration">How long the process ran, as measured by the tool.</param>
    /// <param name="TimedOut">The tool killed the process at its timeout.</param>
    /// <param name="WorkingDirectory">Directory the command ran in.</param>
    public sealed record ShellResult(
        string? Command,
        int? ExitCode,
        string? StandardOutput,
        string? StandardError,
        TimeSpan? Duration,
        bool TimedOut,
        string? WorkingDirectory)
    {
        /// <summary>Read the structured result off a snapshot. Never parses rendered text.</summary>
        public static ShellResult From(ToolCallSnapshot snapshot)
        {
            var data = snapshot.Data;

            // The command is echoed in the result, but the parameters are authoritative and are
            // available before the tool returns, so the header can show it while it runs.
            var command = ToolData.GetString(snapshot.Parameters, "command", "cmd", "command_line", "script")
                       ?? ToolData.GetString(data, "command");

            return new ShellResult(
                Command: command,
                ExitCode: ToolData.GetInt(data, "exit_code") ?? ToolData.GetInt(snapshot.Metadata, "exit_code"),
                StandardOutput: ToolData.GetString(data, "stdout", "output"),
                StandardError: ToolData.GetString(data, "stderr", "error_output"),
                Duration: ToolData.GetDuration(data, "duration_ms") ?? snapshot.Duration,
                TimedOut: ToolData.GetBool(data, "timed_out") ?? false,
                WorkingDirectory: ToolData.GetString(data, "working_directory")
                               ?? ToolData.GetString(snapshot.Parameters, "working_directory", "workdir", "cwd"));
        }

        /// <summary>True when the process reported a non-zero exit.</summary>
        public bool Failed => ExitCode is { } code && code != 0;
    }

    /// <summary>
    /// Renders shell commands (issue #251).
    ///
    /// A command is the highest-traffic tool in a coding session and the one whose result is a
    /// document rather than a fact, so it renders as a block: the highlighted command line, then
    /// its output with stderr distinguished from stdout, then the exit code when it is non-zero.
    /// Long commands wrap instead of being cut at a fixed 60 characters, and the output goes
    /// through the shared formatter, which decodes ANSI and keeps the tail (where a build's
    /// error summary lives) instead of only the head.
    /// </summary>
    public sealed class ShellToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName)
            => toolName is "execute_command" or "bash_command" or "run_command" or "shell";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var result = ShellResult.From(snapshot);
            var header = BuildHeader(snapshot, result, theme);

            if (!snapshot.IsComplete)
            {
                return new ToolPresentation
                {
                    Header = header,
                    Trailing = ToolOutputFormatter.FormatDuration(DateTime.UtcNow - snapshot.StartedAtUtc),
                    Body = WorkingDirectoryNote(result, snapshot, theme),
                    Layout = ToolLayout.Block
                };
            }

            var body = new List<StyledLine>();
            body.AddRange(WorkingDirectoryNote(result, snapshot, theme));
            body.AddRange(OutputRows(snapshot, result, context));

            return new ToolPresentation
            {
                Header = header,
                Trailing = BuildTrailing(snapshot, result),
                Body = body,
                Layout = ToolLayout.Block
            };
        }

        // "Running"/"Ran" plus the highlighted command, the way codex labels its exec cell.
        private static StyledLine BuildHeader(ToolCallSnapshot snapshot, ShellResult result, Themes.Theme theme)
        {
            var verb = snapshot.IsComplete ? "Ran " : "Running ";
            var command = result.Command;

            if (string.IsNullOrWhiteSpace(command))
                return StyledLine.Plain(snapshot.IsComplete ? "Ran a command" : "Running a command", theme.ToolName);

            var highlighted = ShellHighlighter.Highlight(command, theme);
            return highlighted.WithPrefix(new StyledSpan(verb, theme.ToolName, DL.CellAttrFlags.Bold));
        }

        // The exit code earns its place on the header only when it is non-zero; a timeout is
        // called out by name because "exit 124" does not explain itself.
        private static string? BuildTrailing(ToolCallSnapshot snapshot, ShellResult result)
        {
            var parts = new List<string>();

            if (result.TimedOut) parts.Add("timed out");
            else if (result.Failed) parts.Add($"exit {result.ExitCode}");
            else if (!snapshot.IsSuccessful && result.ExitCode is null) parts.Add("failed");

            var duration = result.Duration ?? snapshot.Duration;
            if (duration is { } d && d >= ToolOutputFormatter.MinimumReportedDuration)
                parts.Add(ToolOutputFormatter.FormatDuration(d));

            return parts.Count == 0 ? null : string.Join("  ", parts);
        }

        // Shown only when the command ran somewhere other than the session directory, so the
        // common case stays a single clean line.
        private static IReadOnlyList<StyledLine> WorkingDirectoryNote(
            ShellResult result, ToolCallSnapshot snapshot, Themes.Theme theme)
        {
            var directory = result.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(directory)) return Array.Empty<StyledLine>();

            var display = ToolCallSummarizer.ShortenPath(directory);
            if (string.IsNullOrEmpty(display) || display == ".") return Array.Empty<StyledLine>();

            return new[] { StyledLine.Plain($"in {display}", theme.TextDim, DL.CellAttrFlags.Italic) };
        }

        // stdout and stderr are formatted separately so stderr can carry the error color, then
        // concatenated under one budget. A successful command with nothing to say gets the
        // explicit "(no output)" marker rather than a silently empty block.
        private static IReadOnlyList<StyledLine> OutputRows(
            ToolCallSnapshot snapshot, ShellResult result, ToolPresentationContext context)
        {
            int width = Math.Max(8, context.Width - 4);
            var theme = context.Theme;
            var rows = new List<StyledLine>();

            var stdout = ToolOutputFormatter.Format(result.StandardOutput, width, context.RowBudget, theme);
            rows.AddRange(stdout.Rows);

            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                // stderr gets its own budget: it is usually short and always the part worth reading.
                int errorBudget = Math.Max(2, context.RowBudget / 2);
                var stderr = ToolOutputFormatter.Format(result.StandardError, width, errorBudget, theme);
                rows.AddRange(stderr.Rows.Select(r => Recolor(r, theme.Error)));
            }

            if (rows.Count > 0) return rows;

            // Nothing on either stream. Say why: a failure without output still needs a reason.
            if (snapshot.IsSuccessful && !result.Failed)
                return new[] { ToolOutputFormatter.NoOutputMarker(theme) };

            var reason = snapshot.ErrorMessage ?? snapshot.Message;
            if (!string.IsNullOrWhiteSpace(reason))
                return ToolOutputFormatter.Format(reason, width, context.RowBudget, theme).Rows
                    .Select(r => Recolor(r, theme.Error)).ToList();

            return new[] { StyledLine.Plain("(no output)", theme.Ghost, DL.CellAttrFlags.Italic) };
        }

        // Applies a color to spans that do not already carry one, so ANSI colors a program chose
        // for its own stderr survive while uncolored text picks up the error role.
        private static StyledLine Recolor(StyledLine line, DL.Rgb24 color)
            => new(line.Spans.Select(s => s.Foreground is null ? s with { Foreground = color } : s));
    }
}
