using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Renders directory listings (issue #256).
    ///
    /// The counts come straight from the metadata ListDirectoryTool returns - total_entries,
    /// file_count, directory_count. The previous rendering parsed the JSON result WITH REGEXES
    /// (<c>"type":\s*"directory"</c>, <c>"name":\s*"([^"]+)"</c>) and then decided whether each
    /// entry was a directory by testing <c>!name.Contains(".") &amp;&amp; !name.StartsWith(".")</c>,
    /// a heuristic that calls "Makefile" a directory and "v1.2" a file. A second, correct
    /// implementation existed elsewhere in the same file, so the two disagreed.
    /// </summary>
    public sealed class ListDirectoryToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName) => toolName is "list_directory";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var path = ToolCallSummarizer.ShortenPath(
                snapshot.ResultString("directory_path") ?? snapshot.Argument("path", "directory_path", "directory", "dir", "folder"));

            var verb = snapshot.IsComplete ? "List " : "Listing ";
            var header = new StyledLine(new[]
            {
                new StyledSpan(verb, theme.ToolName, DL.CellAttrFlags.Bold),
                new StyledSpan(string.IsNullOrEmpty(path) ? "current directory" : path, theme.Primary, DL.CellAttrFlags.None)
            });

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            return new ToolPresentation
            {
                Header = header,
                Trailing = BuildTrailing(snapshot),
                Body = BuildEntryRows(snapshot, context)
            };
        }

        // "28 files, 3 directories" - and an explicit "(empty)" rather than a bare count of zero,
        // because an empty directory is a finding, not a missing result.
        private static string BuildTrailing(ToolCallSnapshot snapshot)
        {
            int? files = snapshot.ResultInt("file_count");
            int? directories = snapshot.ResultInt("directory_count");
            int? total = snapshot.ResultInt("total_entries", "count", "total_count");

            if (files is null && directories is null)
                return total is { } t
                    ? (t == 0 ? "(empty)" : ToolOutputFormatter.Pluralize(t, "entry", "entries"))
                    : string.Empty;

            if ((files ?? 0) + (directories ?? 0) == 0) return "(empty)";

            var parts = new List<string>();
            if (files is { } f && f > 0) parts.Add(ToolOutputFormatter.Pluralize(f, "file"));
            if (directories is { } d && d > 0) parts.Add(ToolOutputFormatter.Pluralize(d, "directory", "directories"));
            return string.Join(", ", parts);
        }

        // Entries are the model's input, so collapsed mode shows none of them; expanded lists them
        // with directories first, the way a person reads a directory.
        private static IReadOnlyList<StyledLine> BuildEntryRows(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            if (!context.Expanded) return Array.Empty<StyledLine>();

            var theme = context.Theme;
            var directories = new List<string>();
            var files = new List<string>();

            foreach (var item in snapshot.ResultList("items"))
            {
                if (item is null) continue;
                var name = ToolData.GetString(item, "name", "relative_path", "full_path");
                if (name is null) continue;
                name = System.IO.Path.GetFileName(name.TrimEnd('/', '\\')) is { Length: > 0 } leaf ? leaf : name;

                // The tool states which entries are directories; nothing here guesses from the name.
                if (ToolData.GetBool(item, "is_directory") == true) directories.Add(name + "/");
                else files.Add(name);
            }

            var ordered = directories.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Concat(files.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (ordered.Count == 0) return Array.Empty<StyledLine>();

            int limit = ToolOutputFormatter.ExpandedRowBudget;
            var rows = ordered.Take(limit)
                .Select(n => StyledLine.Plain(n, n.EndsWith("/", StringComparison.Ordinal) ? theme.Primary : theme.ToolResult))
                .ToList();

            if (ordered.Count > limit)
                rows.Add(ToolOutputFormatter.OmissionMarker(ordered.Count - limit, theme));
            return rows;
        }
    }

    /// <summary>
    /// Renders the file-mutation tools (issue #256): create_directory, copy_file, move_file and
    /// delete_file. Each states what changed on one line, with both paths for the two-path
    /// operations. Deletions take the warning color: they are destructive and worth spotting when
    /// scanning back through a session.
    /// </summary>
    public sealed class FileMutationToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName)
            => toolName is "create_directory" or "copy_file" or "move_file" or "delete_file";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var header = snapshot.ToolName switch
            {
                "create_directory" => SinglePath(snapshot, theme, "Create directory ", "Created directory ", theme.Primary),
                "delete_file" => SinglePath(snapshot, theme, "Delete ", "Deleted ", theme.Warning),
                "copy_file" => TwoPaths(snapshot, theme, "Copy ", "Copied "),
                _ => TwoPaths(snapshot, theme, "Move ", "Moved ")
            };

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            return ToolPresentation.Line(header, BuildTrailing(snapshot));
        }

        private static StyledLine SinglePath(ToolCallSnapshot snapshot, Themes.Theme theme,
            string runningVerb, string doneVerb, DL.Rgb24 pathColor)
        {
            var path = ToolCallSummarizer.ShortenPath(
                snapshot.Argument("path", "file_path", "directory", "dir", "folder", "name"));

            return new StyledLine(new[]
            {
                new StyledSpan(snapshot.IsComplete ? doneVerb : runningVerb, theme.ToolName, DL.CellAttrFlags.Bold),
                new StyledSpan(string.IsNullOrEmpty(path) ? "(unspecified)" : path, pathColor, DL.CellAttrFlags.None)
            });
        }

        private static StyledLine TwoPaths(ToolCallSnapshot snapshot, Themes.Theme theme,
            string runningVerb, string doneVerb)
        {
            var source = ToolCallSummarizer.ShortenPath(
                snapshot.Argument("source_path", "source", "src", "from", "file_path", "path"));
            var destination = ToolCallSummarizer.ShortenPath(
                snapshot.Argument("destination_path", "destination", "dest", "to", "target"));

            var spans = new List<StyledSpan>
            {
                new(snapshot.IsComplete ? doneVerb : runningVerb, theme.ToolName, DL.CellAttrFlags.Bold),
                new(string.IsNullOrEmpty(source) ? "(unspecified)" : source, theme.Primary, DL.CellAttrFlags.None)
            };

            if (!string.IsNullOrEmpty(destination))
            {
                spans.Add(new StyledSpan(" -> ", theme.TextDim, DL.CellAttrFlags.None));
                spans.Add(new StyledSpan(destination, theme.Primary, DL.CellAttrFlags.None));
            }
            return new StyledLine(spans);
        }

        // A no-op is worth saying: "created" and "was already there" are different outcomes.
        private static string? BuildTrailing(ToolCallSnapshot snapshot)
        {
            if (snapshot.ResultBool("already_exists", "existed") == true) return "already existed";
            if (snapshot.ResultLong("file_size", "size") is { } bytes && bytes > 0)
                return FormatSize(bytes);
            return null;
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }
    }
}
