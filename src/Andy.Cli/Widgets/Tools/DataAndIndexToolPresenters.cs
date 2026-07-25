using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Renders code index queries (issue #260).
    ///
    /// This tool had the most fragile presentation of any: the executor reflected over the result
    /// graph to build display strings, and the view then RE-PARSED those strings with a battery of
    /// regexes - for indexed-file counts, class and method counts, languages - and guessed the
    /// technology by searching the rendered text for ".csproj" or "package.json". All of it read
    /// text we had generated ourselves one layer earlier.
    ///
    /// The tool returns a typed payload per query_type, so each one is now read directly.
    /// </summary>
    public sealed class CodeIndexToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName) => toolName is "code_index";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var queryType = (snapshot.ResultString("query_type") ?? snapshot.Argument("query_type", "operation") ?? "").ToLowerInvariant();
            var pattern = snapshot.Argument("pattern", "query", "symbol", "name");

            var header = BuildHeader(snapshot, queryType, pattern, theme);

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            // The tool nests its payload one level down under "data".
            ToolData.TryGet(snapshot.Data, "data", out var payload);
            payload ??= snapshot.Data;

            return new ToolPresentation
            {
                Header = header,
                Trailing = BuildTrailing(queryType, payload),
                Body = context.Expanded ? SymbolRows(payload, context) : Array.Empty<StyledLine>()
            };
        }

        private static StyledLine BuildHeader(ToolCallSnapshot snapshot, string queryType, string? pattern, Themes.Theme theme)
        {
            var spans = new List<StyledSpan>();
            switch (queryType)
            {
                case "structure":
                    spans.Add(new StyledSpan("Project structure", theme.ToolName, DL.CellAttrFlags.Bold));
                    break;
                case "hierarchy":
                    spans.Add(new StyledSpan("Type hierarchy", theme.ToolName, DL.CellAttrFlags.Bold));
                    if (pattern is not null)
                    {
                        spans.Add(new StyledSpan(" for ", theme.TextDim));
                        spans.Add(new StyledSpan(pattern, theme.Primary));
                    }
                    break;
                default:
                    spans.Add(new StyledSpan("Search code", theme.ToolName, DL.CellAttrFlags.Bold));
                    if (pattern is not null)
                    {
                        spans.Add(new StyledSpan(" for ", theme.TextDim));
                        spans.Add(new StyledSpan("\"" + pattern + "\"", theme.SyntaxString));
                    }
                    break;
            }

            var scope = ToolData.GetString(snapshot.Parameters, "scope");
            if (scope is not null && scope != "all")
            {
                spans.Add(new StyledSpan(" in ", theme.TextDim));
                spans.Add(new StyledSpan(ToolCallSummarizer.ShortenPath(scope), theme.Primary));
            }

            return new StyledLine(spans);
        }

        // One consistent summary per query type, from typed fields rather than scraped text.
        private static string? BuildTrailing(string queryType, object? payload)
        {
            if (queryType == "structure")
            {
                var parts = new List<string>();
                if (ToolData.GetInt(payload, "file_count") is { } files) parts.Add(ToolOutputFormatter.Pluralize(files, "file"));
                if (ToolData.GetInt(payload, "namespace_count") is { } ns) parts.Add(ToolOutputFormatter.Pluralize(ns, "namespace"));
                if (ToolData.GetInt(payload, "class_count") is { } classes) parts.Add(ToolOutputFormatter.Pluralize(classes, "class", "classes"));
                return parts.Count > 0 ? string.Join(", ", parts) : ToolData.GetString(payload, "summary");
            }

            var symbols = ToolData.GetList(payload, "symbols", "types", "items");
            if (symbols.Count > 0)
            {
                var files = symbols
                    .Select(s => ToolData.GetString(s, "file_path", "filePath"))
                    .Where(p => p is not null)
                    .Distinct()
                    .Count();
                var text = ToolOutputFormatter.Pluralize(symbols.Count, "symbol");
                return files > 0 ? $"{text} in {ToolOutputFormatter.Pluralize(files, "file")}" : text;
            }

            var count = ToolData.GetInt(payload, "count");
            return count is { } n ? (n == 0 ? "(no matches)" : ToolOutputFormatter.Pluralize(n, "result")) : null;
        }

        // "path:line  Kind Name" - enough to go to the definition without opening the index.
        private static IReadOnlyList<StyledLine> SymbolRows(object? payload, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var rows = new List<StyledLine>();

            foreach (var symbol in ToolData.GetList(payload, "symbols", "types", "items").Take(context.RowBudget))
            {
                var name = ToolData.GetString(symbol, "name");
                if (name is null) continue;

                var path = ToolCallSummarizer.ShortenPath(ToolData.GetString(symbol, "file_path", "filePath"));
                var line = ToolData.GetInt(symbol, "line");
                var kind = ToolData.GetString(symbol, "kind");

                var spans = new List<StyledSpan>();
                if (!string.IsNullOrEmpty(path))
                {
                    spans.Add(new StyledSpan(path + (line is { } l ? $":{l}" : ""), theme.Primary));
                    spans.Add(new StyledSpan("  ", theme.TextDim));
                }
                if (kind is not null) spans.Add(new StyledSpan(kind + " ", theme.SyntaxKeyword));
                spans.Add(new StyledSpan(name, theme.ToolResult));

                rows.Add(new StyledLine(spans));
            }

            return rows;
        }
    }

    /// <summary>
    /// Renders the dataframe tools (issue #261).
    ///
    /// Every dataframe operation is tabular by definition, and all of them rendered as a header
    /// plus the first line of a serialized result - so the table, schema or profile was thrown
    /// away. The response envelope is well specified (dataset_id, schema, row_count, preview_rows,
    /// preview_truncated, stats), so each operation can report its shape and show its rows.
    /// </summary>
    public sealed class DataFrameToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName)
            => toolName.StartsWith("dataframe_", StringComparison.Ordinal);

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var header = StyledLine.Plain(
                ToolCallSummarizer.Summarize(snapshot.ToolName, snapshot.Parameters),
                theme.ToolName, DL.CellAttrFlags.Bold);

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var operation = snapshot.ToolName.Substring("dataframe_".Length);
            var body = operation == "schema"
                ? SchemaRows(snapshot, context)
                : PreviewRows(snapshot, context);

            return new ToolPresentation
            {
                Header = header,
                Trailing = BuildTrailing(snapshot),
                Body = body,
                Layout = body.Count > 0 ? ToolLayout.Block : ToolLayout.Inline,
                // Tables align themselves; an outer gutter would shift every column.
                IndentBody = false
            };
        }

        // "1,204 rows, 8 columns" - the shape of the result, which every operation has.
        private static string? BuildTrailing(ToolCallSnapshot snapshot)
        {
            var parts = new List<string>();

            if (snapshot.ResultLong("row_count") is { } rows)
                parts.Add(ToolOutputFormatter.Pluralize(rows, "row"));

            var columns = snapshot.ResultList("schema").Count;
            if (columns > 0) parts.Add(ToolOutputFormatter.Pluralize(columns, "column"));

            var warnings = snapshot.ResultList("warnings");
            if (warnings.Count > 0) parts.Add(ToolOutputFormatter.Pluralize(warnings.Count, "warning"));

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private static IReadOnlyList<StyledLine> PreviewRows(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var preview = snapshot.ResultList("preview_rows");
            if (preview.Count == 0) return Array.Empty<StyledLine>();

            // Column order comes from the schema when there is one, so it matches the dataset
            // rather than whatever order the first row's keys happen to enumerate in.
            var headers = snapshot.ResultList("schema")
                .Select(c => ToolData.GetString(c, "name"))
                .Where(n => n is not null)
                .Select(n => n!)
                .ToList();

            if (headers.Count == 0)
                headers = KeysOf(preview[0]).ToList();
            if (headers.Count == 0) return Array.Empty<StyledLine>();

            var rows = preview
                .Select(row => (IReadOnlyList<string>)headers
                    .Select(h => ToolData.TryGet(row, h, out var value) ? TableRenderer.Cell(value) : "-")
                    .ToList())
                .ToList();

            var table = TableRenderer.Render(headers, rows, context.Width, context.RowBudget, context.Theme).ToList();

            if (snapshot.ResultBool("preview_truncated") == true && table.Count > 0)
                table.Add(StyledLine.Plain("(preview truncated)", context.Theme.Ghost, DL.CellAttrFlags.Italic));

            return table;
        }

        private static IReadOnlyList<StyledLine> SchemaRows(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var schema = snapshot.ResultList("schema");
            if (schema.Count == 0) return Array.Empty<StyledLine>();

            var rows = schema
                .Select(c => (IReadOnlyList<string>)new[]
                {
                    ToolData.GetString(c, "name") ?? "-",
                    ToolData.GetString(c, "type") ?? "-",
                    ToolData.GetBool(c, "nullable") == false ? "not null" : ""
                })
                .ToList();

            return TableRenderer.Render(new[] { "column", "type", "" }, rows,
                context.Width, context.RowBudget, context.Theme);
        }

        private static IEnumerable<string> KeysOf(object? row) => row switch
        {
            IReadOnlyDictionary<string, object?> ro => ro.Keys,
            IDictionary<string, object?> d => d.Keys,
            null => Array.Empty<string>(),
            _ => row.GetType().GetProperties().Select(p => p.Name)
        };
    }

    /// <summary>
    /// Renders the PDF tools (issue #262).
    ///
    /// These had good headers already; what was missing was the quantity each operation produced.
    /// pdf_extract_text in particular showed the first line of the extracted text, which is
    /// usually a page header or a stray page number - so the feed said nothing about how much was
    /// extracted or from where.
    /// </summary>
    public sealed class PdfToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName) => toolName.StartsWith("pdf_", StringComparison.Ordinal);

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var header = StyledLine.Plain(
                ToolCallSummarizer.Summarize(snapshot.ToolName, snapshot.Parameters),
                theme.ToolName, DL.CellAttrFlags.Bold);

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            return new ToolPresentation
            {
                Header = header,
                Trailing = BuildTrailing(snapshot),
                Body = context.Expanded ? Preview(snapshot, context) : Array.Empty<StyledLine>(),
                Layout = ToolLayout.Block
            };
        }

        // Each operation reports what it produced: pages, words, tables, matches.
        private static string? BuildTrailing(ToolCallSnapshot snapshot)
        {
            var parts = new List<string>();

            if (snapshot.ResultInt("page_count", "pages") is { } pages)
                parts.Add(ToolOutputFormatter.Pluralize(pages, "page"));

            // A requested page range matters: it says which part of the document the model saw.
            var from = ToolData.GetInt(snapshot.Parameters, "start_page", "first_page", "from_page");
            var to = ToolData.GetInt(snapshot.Parameters, "end_page", "last_page", "to_page");
            if (from is not null || to is not null) parts.Add($"pages {from?.ToString() ?? "1"}-{to?.ToString() ?? "end"}");

            if (snapshot.ResultInt("word_count", "words") is { } words)
                parts.Add(ToolOutputFormatter.Pluralize(words, "word"));

            var tables = snapshot.ResultList("tables");
            if (tables.Count > 0) parts.Add(ToolOutputFormatter.Pluralize(tables.Count, "table"));

            if (snapshot.ResultInt("match_count", "total_matches") is { } matches)
                parts.Add(ToolOutputFormatter.Pluralize(matches, "match", "matches"));

            var title = snapshot.ResultString("title");
            if (title is not null) parts.Add("\"" + ToolCallSummarizer.Truncate(title, 40) + "\"");

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private static IReadOnlyList<StyledLine> Preview(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            // The outline is a tree and reads as one; everything else previews as text.
            var outline = snapshot.ResultList("outline", "bookmarks");
            if (outline.Count > 0) return OutlineRows(outline, context);

            var text = snapshot.ResultString("text", "content") ?? ToolPresenterHelpers.AsText(snapshot.Data);
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<StyledLine>();

            return ToolOutputFormatter.Format(text, ToolPresenterHelpers.BodyWidth(context),
                context.RowBudget, context.Theme).Rows;
        }

        private static IReadOnlyList<StyledLine> OutlineRows(IReadOnlyList<object?> outline, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var rows = new List<StyledLine>();

            foreach (var entry in outline.Take(context.RowBudget))
            {
                var title = ToolData.GetString(entry, "title", "text", "name");
                if (title is null) continue;

                int level = ToolData.GetInt(entry, "level", "depth") ?? 0;
                var page = ToolData.GetInt(entry, "page", "page_number");

                var spans = new List<StyledSpan>
                {
                    StyledSpan.Plain(new string(' ', Math.Clamp(level, 0, 8) * 2)),
                    new(title, theme.ToolResult)
                };
                if (page is { } p) spans.Add(new StyledSpan($"  p.{p}", theme.TextDim));

                rows.Add(new StyledLine(spans));
            }

            return rows;
        }
    }
}
