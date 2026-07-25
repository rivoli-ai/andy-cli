using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Renders HTTP requests and JSON processing (issue #259).
    ///
    /// The facts you want while watching an agent hit an API - the status code, the response size,
    /// how long it took - are all in the tool's result and none of them were shown; the feed
    /// displayed the first line of the response body instead. The URL was also truncated at 48
    /// characters regardless of terminal width, which usually cut the endpoint off the end.
    /// </summary>
    public sealed class WebToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName) => toolName is "http_request" or "json_processor";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            return snapshot.ToolName == "json_processor"
                ? PresentJson(snapshot, context)
                : PresentHttp(snapshot, context);
        }

        private static ToolPresentation PresentHttp(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var url = snapshot.ResultString("url") ?? snapshot.Argument("url", "uri", "endpoint") ?? "";
            var method = (snapshot.Argument("method", "http_method") ?? "GET").ToUpperInvariant();

            // The URL is elided in the MIDDLE rather than cut at the end, so the host and the
            // endpoint - the two parts that identify the call - both survive on a narrow terminal.
            var display = ElideMiddle(url, Math.Max(20, context.Width - method.Length - 24));

            var header = new StyledLine(new[]
            {
                new StyledSpan(method + " ", theme.ToolName, DL.CellAttrFlags.Bold),
                new StyledSpan(display, theme.Primary)
            });

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var status = snapshot.ResultInt("status_code");
            var trailing = new List<string>();
            if (status is { } code) trailing.Add(code.ToString());
            if (snapshot.ResultLong("content_length") is { } length) trailing.Add(FormatBytes(length));
            if (ToolPresenterHelpers.CompletedTrailing(snapshot) is { } duration) trailing.Add(duration);

            return new ToolPresentation
            {
                Header = StatusColored(header, status, theme),
                Trailing = trailing.Count == 0 ? null : string.Join("  ", trailing),
                Body = BodyPreview(snapshot, context),
                Layout = ToolLayout.Block
            };
        }

        // The status code is the outcome, so it colors the row: 2xx success, 3xx dim, 4xx/5xx error.
        private static StyledLine StatusColored(StyledLine header, int? status, Themes.Theme theme)
        {
            if (status is not { } code) return header;
            var color = code switch
            {
                >= 200 and < 300 => theme.Success,
                >= 300 and < 400 => theme.TextDim,
                _ => theme.Error
            };
            return header.WithSuffix(new StyledSpan("  " + code, color, DL.CellAttrFlags.Bold));
        }

        private static ToolPresentation PresentJson(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var operation = snapshot.Argument("operation", "action");
            var header = StyledLine.Plain(
                operation is null ? "Process JSON" : $"Process JSON ({operation})",
                theme.ToolName, DL.CellAttrFlags.Bold);

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            return new ToolPresentation
            {
                Header = header,
                Trailing = DescribeShape(snapshot.Data),
                Body = BodyPreview(snapshot, context),
                Layout = ToolLayout.Block
            };
        }

        // "12 items" / "4 fields" - what came back, before any of it.
        private static string? DescribeShape(object? data)
        {
            var items = ToolData.AsList(data);
            if (items.Count > 0) return ToolOutputFormatter.Pluralize(items.Count, "item");

            if (data is JsonElement { ValueKind: JsonValueKind.Array } array)
                return ToolOutputFormatter.Pluralize(array.GetArrayLength(), "item");
            if (data is JsonElement { ValueKind: JsonValueKind.Object } obj)
                return ToolOutputFormatter.Pluralize(obj.EnumerateObject().Count(), "field");

            return null;
        }

        // JSON bodies are pretty-printed so they are readable, rather than shown as one long line.
        private static IReadOnlyList<StyledLine> BodyPreview(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var content = snapshot.ResultString("content") ?? ToolPresenterHelpers.AsText(snapshot.Data);
            if (string.IsNullOrWhiteSpace(content)) return Array.Empty<StyledLine>();

            return ToolOutputFormatter.Format(PrettyPrintJson(content),
                ToolPresenterHelpers.BodyWidth(context), context.RowBudget, context.Theme).Rows;
        }

        /// <summary>Re-indent a JSON document; anything else is returned unchanged.</summary>
        public static string PrettyPrintJson(string text)
        {
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return text;

            try
            {
                using var document = JsonDocument.Parse(text);
                return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                // Not valid JSON after all (a truncated body, an HTML error page): show it as it came.
                return text;
            }
        }

        /// <summary>Shorten to <paramref name="max"/> characters by removing from the middle.</summary>
        public static string ElideMiddle(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max || max < 8) return value;
            int keep = max - 3;
            int head = (keep + 1) / 2;
            return value.Substring(0, head) + "..." + value.Substring(value.Length - (keep - head));
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }
    }

    /// <summary>
    /// Renders the small utility tools (issue #265): date_time, encoding_tool, format_text,
    /// system_info and process_info.
    ///
    /// These are low-traffic, so the goal is not elaborate presentation - it is that each states
    /// its ANSWER on one line, instead of a fragment of a serialized object. The executor's
    /// anonymous-type reflection special case, which hunted for a "formatted"/"output"/"result"
    /// property, worked for date_time and produced nothing useful for the rest.
    /// </summary>
    public sealed class UtilityToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName)
            => toolName is "date_time" or "datetime_tool" or "encoding_tool" or "format_text"
                or "system_info" or "process_info";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var header = StyledLine.Plain(Title(snapshot), theme.ToolName, DL.CellAttrFlags.Bold);

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);
            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var answer = Answer(snapshot);
            var body = context.Expanded ? Details(snapshot, context) : Array.Empty<StyledLine>();

            return new ToolPresentation
            {
                Header = answer is null ? header : header.WithSuffix(new StyledSpan("  " + answer, theme.ToolResult)),
                Body = body,
                Layout = body.Count > 1 ? ToolLayout.Block : ToolLayout.Inline
            };
        }

        private static string Title(ToolCallSnapshot snapshot)
        {
            var operation = snapshot.Argument("operation", "action");
            return snapshot.ToolName switch
            {
                "system_info" => "System info",
                "process_info" => "Processes",
                "format_text" => operation is null ? "Format text" : $"Format text ({operation})",
                "encoding_tool" => EncodingTitle(operation),
                _ => "Date/time"
            };
        }

        private static string EncodingTitle(string? operation)
        {
            if (operation is null) return "Encode/decode";
            var lower = operation.ToLowerInvariant();
            if (lower.Contains("decode")) return $"Decode ({operation})";
            if (lower.Contains("hash") || lower.Contains("md5") || lower.Contains("sha") || lower.Contains("bcrypt"))
                return $"Hash ({operation})";
            if (lower.Contains("encode")) return $"Encode ({operation})";
            return $"Transform ({operation})";
        }

        // The tool's own output IS the answer for these; it just has to be kept to one line, and
        // never echoed at full length - a hash or an encoding argument is frequently a secret.
        private static string? Answer(ToolCallSnapshot snapshot)
        {
            if (snapshot.ToolName == "process_info")
            {
                var processes = snapshot.ResultList("items");
                return processes.Count > 0 ? ToolOutputFormatter.Pluralize(processes.Count, "process", "processes") : null;
            }

            if (snapshot.ToolName == "system_info")
            {
                var os = snapshot.ResultString("os_description", "os", "operating_system");
                var arch = snapshot.ResultString("architecture", "os_architecture");
                return os is null ? null : arch is null ? os : $"{os} ({arch})";
            }

            var output = ToolPresenterHelpers.AsText(snapshot.Data);
            if (string.IsNullOrWhiteSpace(output)) return null;

            var firstLine = ToolData.SplitLines(output).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (firstLine is null) return null;

            return ToolCallSummarizer.Truncate(firstLine, MaxAnswerLength);
        }

        /// <summary>
        /// Cap on the inline answer. Hash and encoding results are often long and often derived
        /// from something the user would not want echoed into a transcript at full length.
        /// </summary>
        private const int MaxAnswerLength = 64;

        private static IReadOnlyList<StyledLine> Details(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var text = ToolPresenterHelpers.AsText(snapshot.Data);
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<StyledLine>();

            return ToolOutputFormatter.Format(text, ToolPresenterHelpers.BodyWidth(context),
                context.RowBudget, context.Theme).Rows;
        }
    }

    /// <summary>
    /// Renders skill loading (issue #263).
    ///
    /// Loading a skill changes how the agent behaves for the rest of the turn, so it deserves to
    /// be legible rather than incidental. The skill tools had no summarizer entry at all and fell
    /// through to the generic "humanize the tool id" fallback.
    /// </summary>
    public sealed class SkillToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName) => toolName is "skill" or "skill_file";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var name = snapshot.Argument("name", "skill", "skill_name");
            var spans = new List<StyledSpan>
            {
                new(snapshot.IsComplete ? "Skill " : "Loading skill ", theme.ToolName, DL.CellAttrFlags.Bold),
                new(name is null ? "(unnamed)" : "\"" + name + "\"", theme.Primary)
            };

            if (snapshot.ToolName == "skill_file" && snapshot.Argument("path", "file", "file_path") is { } file)
            {
                spans.Add(new StyledSpan(" read ", theme.TextDim));
                spans.Add(new StyledSpan(ToolCallSummarizer.ShortenPath(file), theme.Primary));
            }

            var header = new StyledLine(spans);

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);

            if (!snapshot.IsSuccessful)
            {
                return new ToolPresentation
                {
                    Header = header,
                    Body = DisabledHint(snapshot, context) ?? ToolPresenterHelpers.ErrorBodyFor(snapshot, context)
                };
            }

            return ToolPresentation.Line(header, snapshot.ResultString("description", "summary") is { } d
                ? ToolCallSummarizer.Truncate(d, ToolCallSummarizer.MaxArgumentLength)
                : null);
        }

        // A disabled skill failing to load is a different problem from a missing one, and the CLI
        // has a command that fixes it.
        private static IReadOnlyList<StyledLine>? DisabledHint(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var error = ToolPresenterHelpers.FailureText(snapshot);
            if (!error.Contains("disabled", StringComparison.OrdinalIgnoreCase)) return null;

            var name = snapshot.Argument("name", "skill", "skill_name");
            return new[]
            {
                StyledLine.Plain(error, context.Theme.Error),
                StyledLine.Plain($"Enable it with /skills enable {name}", context.Theme.TextDim, DL.CellAttrFlags.Italic)
            };
        }
    }
}
