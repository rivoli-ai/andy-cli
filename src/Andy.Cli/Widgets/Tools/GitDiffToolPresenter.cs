using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Renders git diffs (issue #257).
    ///
    /// git_diff used to be treated as a "raw output tool": its output was printed as plain dim
    /// text, five lines collapsed, WITH BLANK LINES FILTERED OUT - which silently removes empty
    /// context lines, so what the user saw was not the diff the tool returned. Nothing was
    /// colored, and the tool's emoji headings went straight into the feed.
    ///
    /// It now renders through the same diff rows as a file edit: per-file header with change
    /// counts, colored signs, row tints and syntax highlighting.
    /// </summary>
    public sealed class GitDiffToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName) => toolName is "git_diff";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var target = ToolCallSummarizer.ShortenPath(
                snapshot.Argument("path", "file_path", "repository_path", "repo_path"));
            var staged = snapshot.ResultBool("staged") ?? ToolData.GetBool(snapshot.Parameters, "staged") ?? false;

            var header = BuildHeader(snapshot, target, staged, theme);

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var files = GitDiffOutputReader.Read(ToolPresenterHelpers.AsText(snapshot.Data) ?? snapshot.Message);

            if (files.Count == 0)
                return NoStructureFallback(snapshot, header, context);

            return new ToolPresentation
            {
                Header = header,
                Trailing = BuildTrailing(files),
                Body = BuildFileRows(files, context),
                Layout = ToolLayout.Block,
                // The diff draws its own gutter; an outer one would misalign the line numbers.
                IndentBody = false
            };
        }

        private static StyledLine BuildHeader(ToolCallSnapshot snapshot, string target, bool staged, Themes.Theme theme)
        {
            var verb = snapshot.IsComplete ? "Diff" : "Diffing";
            var spans = new List<StyledSpan> { new(verb, theme.ToolName, DL.CellAttrFlags.Bold) };

            if (staged) spans.Add(new StyledSpan(" staged", theme.TextDim));
            if (!string.IsNullOrEmpty(target))
            {
                spans.Add(new StyledSpan(" ", theme.TextDim));
                spans.Add(new StyledSpan(target, theme.Primary));
            }
            return new StyledLine(spans);
        }

        // "4 files changed  +212 -87" - the shape of the change before any of its detail.
        private static string BuildTrailing(IReadOnlyList<GitFileDiff> files)
        {
            int added = files.Sum(f => f.Added);
            int removed = files.Sum(f => f.Removed);
            var counts = DiffRenderer.FormatChangeCounts(added, removed);
            return files.Count == 1
                ? counts
                : $"{ToolOutputFormatter.Pluralize(files.Count, "file")} changed  {counts}";
        }

        // Each file gets its own header and its own share of the row budget, so a large first file
        // cannot consume the whole block and hide that three others changed too.
        private static IReadOnlyList<StyledLine> BuildFileRows(
            IReadOnlyList<GitFileDiff> files, ToolPresentationContext context)
        {
            var theme = context.Theme;
            int budget = context.RowBudget;
            int perFile = Math.Max(3, budget / Math.Max(1, files.Count));

            var rows = new List<StyledLine>();
            int shown = 0;

            foreach (var file in files)
            {
                if (rows.Count >= budget)
                {
                    rows.Add(ToolOutputFormatter.OmissionMarker(files.Count - shown, theme));
                    break;
                }

                if (rows.Count > 0) rows.Add(StyledLine.Empty);
                rows.Add(new StyledLine(new[]
                {
                    new StyledSpan(ToolCallSummarizer.ShortenPath(file.Path), theme.Accent, DL.CellAttrFlags.Bold),
                    new StyledSpan("  " + DiffRenderer.FormatChangeCounts(file.Added, file.Removed), theme.TextDim)
                }));

                rows.AddRange(DiffRenderer.RenderDiff(file.Diff, file.Path, context.Width, perFile, theme));
                shown++;
            }

            return rows;
        }

        // The tool returned something this build cannot destructure. Show it as text rather than
        // pretending to parse it - but still through the shared formatter, so blank lines survive
        // and the head and tail are both kept.
        private static ToolPresentation NoStructureFallback(
            ToolCallSnapshot snapshot, StyledLine header, ToolPresentationContext context)
        {
            var text = ToolPresenterHelpers.AsText(snapshot.Data) ?? snapshot.Message;

            if (string.IsNullOrWhiteSpace(text) || text.Contains("No changes", StringComparison.OrdinalIgnoreCase))
            {
                return new ToolPresentation
                {
                    Header = header,
                    Body = new[] { StyledLine.Plain("(no changes)", context.Theme.Ghost, DL.CellAttrFlags.Italic) }
                };
            }

            return new ToolPresentation
            {
                Header = header,
                Body = ToolOutputFormatter.Format(text, ToolPresenterHelpers.BodyWidth(context),
                    context.RowBudget, context.Theme).Rows,
                Layout = ToolLayout.Block
            };
        }
    }
}
