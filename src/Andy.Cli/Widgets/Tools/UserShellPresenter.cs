using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.Shell;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Renders a command the USER ran in shell mode (issue #286).
    ///
    /// It is deliberately NOT <see cref="ShellToolPresenter"/>. A model-invoked command reads
    /// "Ran &lt;command&gt;" because the model is the actor; a user-invoked one has to be
    /// unmistakable at a glance and in a copied transcript, so it keeps the composer's <c>!</c>
    /// marker and carries an explicit "you" attribution on every row. Nothing about the row is
    /// inferred from context - scroll into the middle of a long session and a user command still
    /// says who ran it.
    ///
    /// This presenter is handed directly to a <see cref="ToolCallItem"/> rather than registered in
    /// <see cref="ToolPresenterRegistry"/>: resolution there is by tool id, and the tool id really
    /// is <c>execute_command</c> (that is the point - shell escape goes through the same gated
    /// tool), so a registry entry would hijack the model's own shell rows.
    /// </summary>
    public sealed class UserShellPresenter : IToolPresenter
    {
        /// <summary>
        /// Marker key placed on the snapshot's parameters so anything walking the feed can tell a
        /// user-invoked row from a model-invoked one without inspecting the presenter type.
        /// </summary>
        public const string UserInvokedParameterKey = "__userInvoked";

        /// <summary>The attribution word shown on every user-invoked row.</summary>
        public const string AttributionLabel = "you";

        /// <inheritdoc />
        /// <remarks>
        /// Never claims a tool from the registry. Shell escape rows are constructed with this
        /// presenter explicitly; letting it claim <c>execute_command</c> would take over the
        /// model's shell rows too.
        /// </remarks>
        public bool CanPresent(string toolName) => false;

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var result = snapshot.Data as UserShellCommandResult;
            var command = result?.Command ?? snapshot.Argument("command") ?? string.Empty;

            var header = BuildHeader(command, theme);

            if (!snapshot.IsComplete || result is null)
            {
                return new ToolPresentation
                {
                    Header = header,
                    Trailing = AttributionLabel,
                    Body = DirectoryNote(snapshot.Argument("working_directory"), theme),
                    Layout = ToolLayout.Block
                };
            }

            var body = new List<StyledLine>();
            body.AddRange(DirectoryNote(result.WorkingDirectory, theme));
            body.AddRange(OutputRows(result, context));

            return new ToolPresentation
            {
                Header = header,
                Trailing = BuildTrailing(result),
                Body = body,
                Layout = ToolLayout.Block
            };
        }

        // The composer's "!" marker, carried into the transcript so the row looks like what the
        // user typed. Bold + Warning is the same treatment the shell-mode prompt uses.
        private static StyledLine BuildHeader(string command, Themes.Theme theme)
        {
            var marker = new StyledSpan("! ", theme.Warning, DL.CellAttrFlags.Bold);
            if (string.IsNullOrWhiteSpace(command))
            {
                return new StyledLine(new[] { marker, new StyledSpan("(empty command)", theme.Ghost, DL.CellAttrFlags.Italic) });
            }

            var highlighted = ShellHighlighter.Highlight(command, theme);
            return highlighted.WithPrefix(marker);
        }

        // Attribution first, then only the facts that earn their place: a non-zero exit, a timeout
        // or denial by name, and the duration once it is long enough to be interesting.
        private static string BuildTrailing(UserShellCommandResult result)
        {
            var parts = new List<string> { AttributionLabel };

            if (result.Outcome != UserShellOutcome.Succeeded)
            {
                parts.Add(result.StatusLabel);
            }

            if (result.WasTruncated)
            {
                var dropped = result.StandardOutputTruncated + result.StandardErrorTruncated;
                parts.Add(string.Format(CultureInfo.InvariantCulture, "+{0:N0} chars omitted", dropped));
            }

            if (result.Duration >= ToolOutputFormatter.MinimumReportedDuration)
            {
                parts.Add(ToolOutputFormatter.FormatDuration(result.Duration));
            }

            return string.Join("  ", parts);
        }

        // Shown only when the command ran somewhere other than the session directory, matching the
        // model-invoked shell row so the two stay comparable.
        private static IReadOnlyList<StyledLine> DirectoryNote(string? directory, Themes.Theme theme)
        {
            if (string.IsNullOrWhiteSpace(directory)) return Array.Empty<StyledLine>();

            var display = ToolCallSummarizer.ShortenPath(directory);
            if (string.IsNullOrEmpty(display) || display == ".") return Array.Empty<StyledLine>();

            return new[] { StyledLine.Plain($"in {display}", theme.TextDim, DL.CellAttrFlags.Italic) };
        }

        private static IReadOnlyList<StyledLine> OutputRows(UserShellCommandResult result, ToolPresentationContext context)
        {
            int width = Math.Max(8, context.Width - 4);
            var theme = context.Theme;
            var rows = new List<StyledLine>();

            var stdout = ToolOutputFormatter.Format(result.StandardOutput, width, context.RowBudget, theme);
            rows.AddRange(stdout.Rows);

            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                int errorBudget = Math.Max(2, context.RowBudget / 2);
                var stderr = ToolOutputFormatter.Format(result.StandardError, width, errorBudget, theme);
                rows.AddRange(stderr.Rows.Select(r => Recolor(r, theme.Error)));
            }

            if (rows.Count > 0) return rows;

            // Nothing on either stream: say why rather than leaving an empty block. A denial in
            // particular has to explain itself, or it looks like a command that silently did nothing.
            return result.Outcome switch
            {
                UserShellOutcome.Succeeded => new[] { ToolOutputFormatter.NoOutputMarker(theme) },
                UserShellOutcome.Denied => new[] { StyledLine.Plain(
                    "Blocked by the permission rules. Review them with /permissions.", theme.Warning) },
                UserShellOutcome.Cancelled => new[] { StyledLine.Plain(
                    result.TimedOut ? "Timed out; the process was stopped." : "Interrupted.", theme.Warning) },
                UserShellOutcome.Disabled => new[] { StyledLine.Plain(
                    result.ErrorMessage ?? "Shell escape is disabled.", theme.Warning) },
                _ => ErrorRows(result, width, context, theme)
            };
        }

        private static IReadOnlyList<StyledLine> ErrorRows(
            UserShellCommandResult result, int width, ToolPresentationContext context, Themes.Theme theme)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                return ToolOutputFormatter.Format(result.ErrorMessage, width, context.RowBudget, theme).Rows
                    .Select(r => Recolor(r, theme.Error)).ToList();
            }
            return new[] { StyledLine.Plain("(no output)", theme.Ghost, DL.CellAttrFlags.Italic) };
        }

        // Applies a color to spans that do not already carry one, so ANSI colors a program chose
        // for its own stderr survive while uncolored text picks up the error role.
        private static StyledLine Recolor(StyledLine line, DL.Rgb24 color)
            => new(line.Spans.Select(s => s.Foreground is null ? s with { Foreground = color } : s));
    }
}
