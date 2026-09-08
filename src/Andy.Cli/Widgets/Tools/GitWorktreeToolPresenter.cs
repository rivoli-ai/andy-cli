using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Renders the git worktree family (issue #298): git_worktree_list, git_worktree_add,
    /// git_worktree_remove and git_worktree_prune. List shows one row per worktree with its
    /// branch and state; add and remove state the affected path on one line, with removal in
    /// the warning color like other destructive tools.
    /// </summary>
    public sealed class GitWorktreeToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName)
            => toolName is "git_worktree_list" or "git_worktree_add" or "git_worktree_remove" or "git_worktree_prune";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            return snapshot.ToolName switch
            {
                "git_worktree_list" => PresentList(snapshot, context),
                "git_worktree_add" => PresentAdd(snapshot, context),
                "git_worktree_remove" => PresentRemove(snapshot, context),
                _ => PresentPrune(snapshot, context)
            };
        }

        private static ToolPresentation PresentList(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var header = new StyledLine(new[]
            {
                new StyledSpan(snapshot.IsComplete ? "Worktrees" : "Listing worktrees", theme.ToolName, DL.CellAttrFlags.Bold)
            });

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var entries = snapshot.ResultList("items").Where(i => i is not null).ToList();
            return new ToolPresentation
            {
                Header = header,
                Trailing = ToolOutputFormatter.Pluralize(
                    snapshot.ResultInt("count") ?? entries.Count, "worktree"),
                Body = BuildWorktreeRows(entries, context)
            };
        }

        private static IReadOnlyList<StyledLine> BuildWorktreeRows(
            IReadOnlyList<object?> entries, ToolPresentationContext context)
        {
            if (!context.Expanded) return Array.Empty<StyledLine>();

            var theme = context.Theme;
            var rows = new List<StyledLine>();
            int limit = ToolOutputFormatter.ExpandedRowBudget;

            foreach (var entry in entries.Take(limit))
            {
                var path = ToolData.GetString(entry, "path");
                if (path is null) continue;

                var spans = new List<StyledSpan>
                {
                    new(ToolCallSummarizer.ShortenPath(path), theme.Primary, DL.CellAttrFlags.None)
                };

                var branch = ToolData.GetString(entry, "branch");
                spans.Add(new StyledSpan(
                    "  " + (branch ?? (ToolData.GetBool(entry, "is_detached") == true ? "(detached)" : "")),
                    theme.Accent, DL.CellAttrFlags.None));

                var flags = new List<string>();
                if (ToolData.GetBool(entry, "is_main") == true) flags.Add("main");
                if (ToolData.GetBool(entry, "is_bare") == true) flags.Add("bare");
                if (ToolData.GetBool(entry, "is_locked") == true) flags.Add("locked");
                if (ToolData.GetBool(entry, "is_prunable") == true) flags.Add("prunable");
                if (flags.Count > 0)
                    spans.Add(new StyledSpan("  [" + string.Join(", ", flags) + "]", theme.TextDim, DL.CellAttrFlags.None));

                rows.Add(new StyledLine(spans));
            }

            if (entries.Count > limit)
                rows.Add(ToolOutputFormatter.OmissionMarker(entries.Count - limit, theme));
            return rows;
        }

        private static ToolPresentation PresentAdd(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var path = ToolCallSummarizer.ShortenPath(
                snapshot.ResultString("directory_path") ?? snapshot.Argument("path"));

            var header = new StyledLine(new[]
            {
                new StyledSpan(snapshot.IsComplete ? "Added worktree " : "Adding worktree ", theme.ToolName, DL.CellAttrFlags.Bold),
                new StyledSpan(string.IsNullOrEmpty(path) ? "(unspecified)" : path, theme.Primary, DL.CellAttrFlags.None)
            });

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var branch = snapshot.ResultString("branch")
                ?? ToolData.GetString(snapshot.Parameters, "branch");
            var trailing = snapshot.ResultBool("is_detached") == true
                ? "detached"
                : string.IsNullOrEmpty(branch) ? null : "on " + branch;
            return ToolPresentation.Line(header, trailing);
        }

        private static ToolPresentation PresentRemove(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var path = ToolCallSummarizer.ShortenPath(
                snapshot.ResultString("removed_path") ?? snapshot.Argument("path"));

            var header = new StyledLine(new[]
            {
                new StyledSpan(snapshot.IsComplete ? "Removed worktree " : "Removing worktree ", theme.ToolName, DL.CellAttrFlags.Bold),
                new StyledSpan(string.IsNullOrEmpty(path) ? "(unspecified)" : path, theme.Warning, DL.CellAttrFlags.None)
            });

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            return ToolPresentation.Line(header, snapshot.ResultBool("forced") == true ? "forced" : null);
        }

        private static ToolPresentation PresentPrune(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            bool dryRun = ToolData.GetBool(snapshot.Parameters, "dry_run") == true;
            var header = new StyledLine(new[]
            {
                new StyledSpan(
                    snapshot.IsComplete
                        ? (dryRun ? "Checked prunable worktrees" : "Pruned worktrees")
                        : "Pruning worktrees",
                    theme.ToolName, DL.CellAttrFlags.Bold)
            });

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var pruned = snapshot.ResultList("pruned").Count;
            var trailing = pruned == 0
                ? "nothing to prune"
                : ToolOutputFormatter.Pluralize(pruned, "stale registration");
            return ToolPresentation.Line(header, trailing);
        }
    }
}
