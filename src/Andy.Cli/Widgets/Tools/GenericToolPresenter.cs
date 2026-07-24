using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// The presenter for tools without one of their own (issue #264). It is deliberately the
    /// least clever renderer in the set: a summarized header, the primitive arguments, and a
    /// bounded body.
    ///
    /// Even here nothing is scraped from rendered text - a structured payload is pretty-printed
    /// as JSON rather than shown as a truncated first line of a serialized object.
    /// </summary>
    public class GenericToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public virtual bool CanPresent(string toolName) => true;

        /// <inheritdoc />
        public virtual ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var header = BuildHeader(snapshot, context);

            if (!snapshot.IsComplete)
                return ToolPresentation.Line(header, RunningTrailing(snapshot));

            var body = BuildBody(snapshot, context);
            return new ToolPresentation
            {
                Header = header,
                Trailing = CompletedTrailing(snapshot),
                Body = body,
                Layout = body.Count > 1 ? ToolLayout.Block : ToolLayout.Inline
            };
        }

        /// <summary>
        /// The header sentence. Collapsed shows the human-readable action
        /// ("Reading src/Program.cs"); expanded shows the tool id with its primitive arguments,
        /// the way opencode's generic tool row does.
        /// </summary>
        protected virtual StyledLine BuildHeader(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            if (!context.Expanded)
                return StyledLine.Plain(ToolCallSummarizer.Summarize(snapshot.ToolName, snapshot.Parameters));

            var arguments = FormatArguments(snapshot.Parameters);
            var spans = new List<StyledSpan>
            {
                new(snapshot.ToolName, context.Theme.ToolName, DL.CellAttrFlags.Bold)
            };
            if (!string.IsNullOrEmpty(arguments))
                spans.Add(new StyledSpan(" " + arguments, context.Theme.TextDim, DL.CellAttrFlags.None));
            return new StyledLine(spans);
        }

        /// <summary>Body rows for a completed call.</summary>
        protected virtual IReadOnlyList<StyledLine> BuildBody(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            int width = BodyWidth(context);

            if (!snapshot.IsSuccessful)
                return ErrorBody(snapshot, context);

            var text = SuccessBodyText(snapshot);
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<StyledLine>();

            return ToolOutputFormatter.Format(text, width, context.RowBudget, context.Theme).Rows;
        }

        /// <summary>Error rows, colored with the theme's error role.</summary>
        protected static IReadOnlyList<StyledLine> ErrorBody(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var text = FailureText(snapshot);
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<StyledLine>();

            var rows = ToolOutputFormatter.Format(text, BodyWidth(context), context.RowBudget, context.Theme).Rows;
            // Re-color: a tool's own error text has no ANSI styling of its own to preserve.
            return rows.Select(r => StyledLine.Plain(r.Text, context.Theme.Error)).ToList();
        }

        /// <summary>
        /// The message shown when a call did not succeed. Denied and cancelled calls are stated
        /// as such rather than as generic failures (#264).
        /// </summary>
        protected static string FailureText(ToolCallSnapshot snapshot)
        {
            if (snapshot.WasDenied)
                return snapshot.ErrorMessage ?? "Denied by the permission gate; the tool did not run.";
            if (snapshot.WasCancelled)
                return snapshot.ErrorMessage ?? "Interrupted before it finished.";
            return snapshot.ErrorMessage
                ?? snapshot.Message
                ?? AsText(snapshot.Data)
                ?? "The tool reported a failure without a message.";
        }

        /// <summary>Columns available to body rows once the gutter is accounted for.</summary>
        protected static int BodyWidth(ToolPresentationContext context)
            => Math.Max(8, context.Width - 4);

        /// <summary>Duration shown on a completed header, once it is long enough to be information.</summary>
        protected static string? CompletedTrailing(ToolCallSnapshot snapshot)
            => snapshot.Duration is { } d && d >= ToolOutputFormatter.MinimumReportedDuration
                ? ToolOutputFormatter.FormatDuration(d)
                : null;

        /// <summary>Live elapsed clock for a call still in flight.</summary>
        protected static string RunningTrailing(ToolCallSnapshot snapshot)
            => ToolOutputFormatter.FormatDuration(DateTime.UtcNow - snapshot.StartedAtUtc);

        /// <summary>
        /// Text for a successful generic result: a string payload as-is, a structured payload
        /// pretty-printed, otherwise the tool's own message.
        /// </summary>
        private static string? SuccessBodyText(ToolCallSnapshot snapshot)
            => AsText(snapshot.Data) ?? snapshot.Message;

        /// <summary>
        /// Render a payload for display. Strings pass through; anything structured is serialized
        /// with indentation so it is readable instead of appearing as one truncated line.
        /// </summary>
        protected static string? AsText(object? data)
        {
            switch (data)
            {
                case null:
                    return null;
                case string s:
                    return s;
                case JsonElement json:
                    return json.ToString();
            }

            try
            {
                return JsonSerializer.Serialize(data, PrettyJson);
            }
            catch
            {
                // Payloads that will not serialize (cyclic graphs, unsupported types) still get a
                // best-effort rendering rather than disappearing from the feed.
                var text = data.ToString();
                return text is null || text.StartsWith("System.", StringComparison.Ordinal) ? null : text;
            }
        }

        private static readonly JsonSerializerOptions PrettyJson = new()
        {
            WriteIndented = true,
            // Tool payloads are display-only here; unmappable members should not throw.
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// "[key=value, key=value]" over the primitive arguments, matching opencode's generic row.
        /// Structured arguments are summarized by shape instead of being dropped silently, so the
        /// user can still tell that a list of twelve things was passed.
        /// </summary>
        protected static string FormatArguments(IReadOnlyDictionary<string, object?> parameters)
        {
            if (parameters.Count == 0) return string.Empty;

            var parts = new List<string>();
            foreach (var kv in parameters)
            {
                if (kv.Key.StartsWith("__", StringComparison.Ordinal)) continue;
                parts.Add($"{kv.Key}={DescribeValue(kv.Value)}");
            }
            return parts.Count == 0 ? string.Empty : "[" + string.Join(", ", parts) + "]";
        }

        private static string DescribeValue(object? value)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string s:
                    return ToolCallSummarizer.Truncate(s, ToolCallSummarizer.MaxArgumentLength);
                case bool or int or long or short or byte or double or float or decimal:
                    return value.ToString() ?? "";
            }

            var items = ToolData.AsList(value);
            if (items.Count > 0) return $"[{ToolOutputFormatter.Pluralize(items.Count, "item")}]";

            // A structured argument: say how many fields it has rather than dumping it inline.
            int fields = 0;
            if (value is IReadOnlyDictionary<string, object?> ro) fields = ro.Count;
            else if (value is IDictionary<string, object?> d) fields = d.Count;
            else fields = value.GetType().GetProperties().Length;
            return fields > 0 ? $"{{{ToolOutputFormatter.Pluralize(fields, "field")}}}" : "{}";
        }
    }
}
