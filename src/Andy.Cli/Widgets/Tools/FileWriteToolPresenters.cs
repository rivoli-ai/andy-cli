using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Base for the tools that change a file's contents (issues #253, #254). They differ only in
    /// wording and in what their header reports, so the diff rendering lives here once.
    /// </summary>
    public abstract class FileChangeToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public abstract bool CanPresent(string toolName);

        /// <summary>The path the tool was pointed at.</summary>
        protected abstract string? TargetPath(ToolCallSnapshot snapshot);

        /// <summary>Header wording while the call is in flight and once it has finished.</summary>
        protected abstract (string Running, string Done) Verbs(ToolCallSnapshot snapshot);

        /// <summary>Extra facts for the header's trailing metric, beyond the +/- counts.</summary>
        protected virtual IEnumerable<string> ExtraTrailing(ToolCallSnapshot snapshot) => Array.Empty<string>();

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var mutation = snapshot.FileMutation;
            var path = mutation?.DisplayPath ?? ToolCallSummarizer.ShortenPath(TargetPath(snapshot));
            var header = BuildHeader(snapshot, mutation, path, theme);

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);

            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var body = BuildBody(snapshot, mutation, context);
            return new ToolPresentation
            {
                Header = header,
                Trailing = BuildTrailing(snapshot, mutation),
                Body = body,
                Layout = body.Count > 0 ? ToolLayout.Block : ToolLayout.Inline,
                // The diff draws its own gutter and row tints; an outer gutter on top of that
                // would push the tint off the left edge and misalign the line numbers.
                IndentBody = false
            };
        }

        private StyledLine BuildHeader(ToolCallSnapshot snapshot, FileMutationView? mutation, string path, Themes.Theme theme)
        {
            var (running, done) = Verbs(snapshot);
            var verb = snapshot.IsComplete ? done : running;

            // A creation reads as a creation, not as an update that happened to add every line.
            if (snapshot.IsComplete && mutation?.Kind == FileChangeKind.Create)
                verb = "Created ";

            return new StyledLine(new[]
            {
                new StyledSpan(verb, theme.ToolName, DL.CellAttrFlags.Bold),
                new StyledSpan(string.IsNullOrEmpty(path) ? "a file" : path, theme.Primary)
            });
        }

        private string? BuildTrailing(ToolCallSnapshot snapshot, FileMutationView? mutation)
        {
            var parts = new List<string>(ExtraTrailing(snapshot));

            if (mutation is not null)
            {
                parts.Add(mutation.Kind == FileChangeKind.Create && mutation.Diff.RemovedCount == 0
                    ? ToolOutputFormatter.Pluralize(mutation.Diff.AddedCount, "line")
                    : DiffRenderer.FormatChangeCounts(mutation.Diff.AddedCount, mutation.Diff.RemovedCount));
            }

            return parts.Count == 0 ? null : string.Join("  ", parts);
        }

        private static IReadOnlyList<StyledLine> BuildBody(
            ToolCallSnapshot snapshot, FileMutationView? mutation, ToolPresentationContext context)
        {
            if (mutation is null) return Array.Empty<StyledLine>();

            int width = context.Width;
            int budget = context.RowBudget;

            // A new file is shown as numbered content; an edit is shown as a diff.
            if (mutation.Kind == FileChangeKind.Create && mutation.Content is not null)
                return DiffRenderer.RenderContent(mutation.Content, mutation.DisplayPath, width, budget, context.Theme);

            return DiffRenderer.RenderDiff(mutation.Diff, mutation.DisplayPath, width, budget, context.Theme);
        }
    }

    /// <summary>
    /// Renders file writes (issue #253). A creation shows the new file with a line-number gutter;
    /// an overwrite shows the diff against what was there before.
    /// </summary>
    public sealed class WriteFileToolPresenter : FileChangeToolPresenter
    {
        /// <inheritdoc />
        public override bool CanPresent(string toolName) => toolName is "write_file" or "edit_file";

        /// <inheritdoc />
        protected override string? TargetPath(ToolCallSnapshot snapshot)
            => snapshot.Argument("file_path", "path", "filepath", "file", "filename");

        /// <inheritdoc />
        protected override (string Running, string Done) Verbs(ToolCallSnapshot snapshot)
            => ("Writing ", "Wrote ");
    }

    /// <summary>
    /// Renders in-place edits (issue #254).
    ///
    /// replace_text was the worst-served tool in the feed: the completed-tool summary branch keys
    /// off tool names containing "Update", "Edit" or "Write", none of which match "replace_text",
    /// so an edit rendered as a header plus one line of raw result and never showed what changed.
    /// </summary>
    public sealed class ReplaceTextToolPresenter : FileChangeToolPresenter
    {
        /// <inheritdoc />
        public override bool CanPresent(string toolName) => toolName is "replace_text";

        /// <inheritdoc />
        protected override string? TargetPath(ToolCallSnapshot snapshot)
            => snapshot.ResultString("target_path") ?? snapshot.Argument("target_path", "file_path", "path", "file");

        /// <inheritdoc />
        protected override (string Running, string Done) Verbs(ToolCallSnapshot snapshot)
            => ("Editing ", "Edited ");

        /// <inheritdoc />
        protected override IEnumerable<string> ExtraTrailing(ToolCallSnapshot snapshot)
        {
            // How many occurrences were actually replaced is the question an edit raises, and the
            // tool answers it; "0 replacements" is a distinct outcome from a failure and must not
            // render as a bare success.
            var replacements = snapshot.ResultInt("total_replacements", "replacements", "replacement_count", "count");
            if (replacements is { } n)
                yield return n == 0 ? "no matches" : ToolOutputFormatter.Pluralize(n, "replacement");

            var files = snapshot.ResultInt("files_modified", "files_changed");
            if (files is { } f && f > 1) yield return ToolOutputFormatter.Pluralize(f, "file");
        }
    }
}
