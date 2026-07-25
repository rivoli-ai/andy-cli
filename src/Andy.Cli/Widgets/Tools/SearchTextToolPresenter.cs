using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>One search hit, read from the SearchMatch objects search_text returns.</summary>
    /// <param name="FilePath">File the match was found in.</param>
    /// <param name="LineNumber">1-based line, when the tool recorded one.</param>
    /// <param name="Line">The full matching line.</param>
    /// <param name="MatchText">The matched substring, used to highlight within the line.</param>
    public sealed record SearchHit(string FilePath, int? LineNumber, string Line, string MatchText)
    {
        /// <summary>Read the hits off a completed snapshot.</summary>
        public static IReadOnlyList<SearchHit> From(ToolCallSnapshot snapshot)
        {
            var hits = new List<SearchHit>();
            foreach (var item in snapshot.ResultList("items", "matches", "results"))
            {
                if (item is null) continue;
                var path = ToolData.GetString(item, "file_path", "path", "file");
                if (path is null) continue;

                hits.Add(new SearchHit(
                    FilePath: path,
                    LineNumber: ToolData.GetInt(item, "line_number", "line"),
                    Line: ToolData.GetString(item, "full_line", "line_text", "content") ?? string.Empty,
                    MatchText: ToolData.GetString(item, "match_text", "match") ?? string.Empty));
            }
            return hits;
        }
    }

    /// <summary>
    /// Renders text searches (issue #255).
    ///
    /// The counts come from the metadata search_text returns - total_matches,
    /// files_with_matches, files_searched, results_truncated - instead of the previous
    /// "N matches found" string, which was built in the executor from whichever of
    /// <c>count</c> or <c>items.Count</c> happened to be present.
    ///
    /// The matches themselves never reached the feed at all, so a user watching a session could
    /// see that the agent found twelve things but not what they were. Expanded mode now lists
    /// them as path:line with the matched text highlighted.
    /// </summary>
    public sealed class SearchTextToolPresenter : IToolPresenter
    {
        /// <summary>Hits listed in collapsed mode - enough to recognize the result, not enough to flood.</summary>
        private const int CollapsedHitLimit = 3;

        /// <inheritdoc />
        public bool CanPresent(string toolName) => toolName is "search_text" or "search_files";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var pattern = snapshot.ResultString("search_pattern")
                       ?? snapshot.Argument("search_pattern", "pattern", "query", "search", "text");
            var target = ToolCallSummarizer.ShortenPath(
                snapshot.ResultString("target_path") ?? snapshot.Argument("search_path", "path", "directory", "target_path"));

            var header = BuildHeader(snapshot, pattern, target, theme);

            if (!snapshot.IsComplete)
                return ToolPresentation.Line(header);

            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var matches = snapshot.ResultInt("total_matches") ?? snapshot.ResultInt("count") ?? 0;

            // Zero matches is a real, useful outcome and must not look like a silent success.
            if (matches == 0)
            {
                return new ToolPresentation
                {
                    Header = header,
                    Body = new[] { StyledLine.Plain("(no matches)", theme.Warning, DL.CellAttrFlags.Italic) }
                };
            }

            return new ToolPresentation
            {
                Header = header,
                Trailing = BuildTrailing(snapshot, matches),
                Body = BuildHitRows(snapshot, context)
            };
        }

        private static StyledLine BuildHeader(ToolCallSnapshot snapshot, string? pattern, string target, Themes.Theme theme)
        {
            var verb = snapshot.IsComplete ? "Search " : "Searching ";
            var spans = new List<StyledSpan> { new(verb, theme.ToolName, DL.CellAttrFlags.Bold) };

            if (string.IsNullOrEmpty(pattern))
            {
                spans.Add(StyledSpan.Plain("text"));
            }
            else
            {
                spans.Add(new StyledSpan("\"" + ToolCallSummarizer.Truncate(pattern, ToolCallSummarizer.MaxArgumentLength) + "\"",
                    theme.SyntaxString, DL.CellAttrFlags.None));
            }

            if (!string.IsNullOrEmpty(target))
            {
                spans.Add(new StyledSpan(" in ", theme.TextDim, DL.CellAttrFlags.None));
                spans.Add(new StyledSpan(target, theme.Primary, DL.CellAttrFlags.None));
            }

            return new StyledLine(spans);
        }

        // "12 matches in 4 files", plus a truncation note when the tool hit its result cap - the
        // difference between "12 matches" and "at least 12 matches" changes what the count means.
        private static string BuildTrailing(ToolCallSnapshot snapshot, int matches)
        {
            var text = ToolOutputFormatter.Pluralize(matches, "match", "matches");

            if (snapshot.ResultInt("files_with_matches") is { } files && files > 0)
                text += $" in {ToolOutputFormatter.Pluralize(files, "file")}";

            if (snapshot.ResultBool("results_truncated") == true)
                text += ", capped";

            return text;
        }

        // "path:line: matching text", with the matched substring picked out of the line.
        private IReadOnlyList<StyledLine> BuildHitRows(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var hits = SearchHit.From(snapshot);
            if (hits.Count == 0) return Array.Empty<StyledLine>();

            var theme = context.Theme;
            int limit = context.Expanded ? ToolOutputFormatter.ExpandedRowBudget : CollapsedHitLimit;
            int width = ToolPresenterHelpers.BodyWidth(context);

            var rows = new List<StyledLine>();
            foreach (var hit in hits.Take(limit))
            {
                var location = ToolCallSummarizer.ShortenPath(hit.FilePath)
                             + (hit.LineNumber is { } n ? $":{n}" : "");

                var spans = new List<StyledSpan>
                {
                    new(location, theme.Primary, DL.CellAttrFlags.None),
                    new(": ", theme.TextDim, DL.CellAttrFlags.None)
                };
                spans.AddRange(HighlightMatch(hit, width - location.Length - 2, theme));
                rows.Add(new StyledLine(spans));
            }

            if (hits.Count > limit)
                rows.Add(ToolOutputFormatter.OmissionMarker(hits.Count - limit, theme));

            return rows;
        }

        // The matched substring is picked out inside its line so the eye lands on it, which is
        // the whole reason for showing the line rather than just the count.
        private static IEnumerable<StyledSpan> HighlightMatch(SearchHit hit, int width, Themes.Theme theme)
        {
            var line = hit.Line.Trim();
            if (width < 8) width = 8;
            if (line.Length > width) line = line.Substring(0, Math.Max(0, width - 3)) + "...";

            if (string.IsNullOrEmpty(hit.MatchText))
            {
                yield return StyledSpan.Plain(line);
                yield break;
            }

            int index = line.IndexOf(hit.MatchText, StringComparison.Ordinal);
            if (index < 0)
            {
                yield return StyledSpan.Plain(line);
                yield break;
            }

            if (index > 0) yield return StyledSpan.Plain(line.Substring(0, index));
            yield return new StyledSpan(hit.MatchText, theme.Warning, DL.CellAttrFlags.Bold);
            int after = index + hit.MatchText.Length;
            if (after < line.Length) yield return StyledSpan.Plain(line.Substring(after));
        }
    }
}
