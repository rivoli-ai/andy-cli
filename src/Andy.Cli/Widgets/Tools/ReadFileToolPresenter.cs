using System;
using System.Collections.Generic;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Renders file reads (issue #252).
    ///
    /// A read is a fact, not a document: the content went to the model, and repeating it in the
    /// feed would bury the conversation. So this is a single inline row stating what was read and
    /// how much of it.
    ///
    /// Every number comes from the metadata ReadFileTool returns (line_count,
    /// file_size_formatted, encoding, start_line, end_line). The previous rendering recovered the
    /// same numbers by running the regex <c>(\d+)\s+lines</c> over a string that an earlier layer
    /// had itself formatted from those very fields.
    /// </summary>
    public sealed class ReadFileToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName) => toolName is "read_file";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var path = ToolCallSummarizer.ShortenPath(
                snapshot.Argument("file_path", "path", "filepath", "file", "filename"));

            var header = BuildHeader(snapshot, path, theme);

            if (!snapshot.IsComplete)
                return ToolPresentation.Line(header);

            if (!snapshot.IsSuccessful)
            {
                return new ToolPresentation
                {
                    Header = header,
                    Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context)
                };
            }

            // A successful read gets no body: the file content is the model's input, not the
            // user's output. The facts worth knowing ride on the header instead.
            return ToolPresentation.Line(header, BuildTrailing(snapshot, context));
        }

        private static StyledLine BuildHeader(ToolCallSnapshot snapshot, string path, Themes.Theme theme)
        {
            var verb = snapshot.IsComplete ? "Read " : "Reading ";
            if (string.IsNullOrEmpty(path))
                return StyledLine.Plain(verb.TrimEnd() + " a file", theme.ToolName);

            var spans = new List<StyledSpan>
            {
                new(verb, theme.ToolName, DL.CellAttrFlags.Bold),
                new(path, theme.Primary, DL.CellAttrFlags.None)
            };

            // A partial read says so on the header: "(truncated)" never told the user WHICH part
            // of the file the model actually saw.
            var range = FormatRange(snapshot);
            if (range != null) spans.Add(new StyledSpan(range, theme.TextDim, DL.CellAttrFlags.None));

            return new StyledLine(spans);
        }

        // ":100-150" when a range was requested, ":100-" / ":-150" for an open-ended one.
        private static string? FormatRange(ToolCallSnapshot snapshot)
        {
            var start = snapshot.ResultInt("start_line") ?? ToolData.GetInt(snapshot.Parameters, "start_line", "offset");
            var end = snapshot.ResultInt("end_line") ?? ToolData.GetInt(snapshot.Parameters, "end_line");

            if (start is null && end is null) return null;
            return $":{start?.ToString() ?? ""}-{end?.ToString() ?? ""}";
        }

        // Line count, size and a non-default encoding - each only when the tool reported it.
        private static string? BuildTrailing(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var parts = new List<string>();

            if (snapshot.ResultInt("line_count") is { } lines)
                parts.Add(ToolOutputFormatter.Pluralize(lines, "line"));

            var size = snapshot.ResultString("file_size_formatted");
            if (size != null) parts.Add(size);

            // Only worth saying when it is not the everyday case.
            var encoding = snapshot.ResultString("encoding");
            if (encoding != null && !encoding.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
                && !encoding.Contains("Unicode (UTF-8)", StringComparison.OrdinalIgnoreCase))
                parts.Add(encoding);

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }
}
