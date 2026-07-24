using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Andy.Cli.Services;
using Andy.Cli.Services.ToolResults;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Presentation pieces every presenter needs: failure bodies, trailing metrics, argument
    /// formatting. Kept as free functions rather than a base class so a presenter can use what it
    /// needs without inheriting a layout it does not want.
    /// </summary>
    public static class ToolPresenterHelpers
    {
        /// <summary>Columns available to body rows once the gutter is accounted for.</summary>
        public static int BodyWidth(ToolPresentationContext context) => Math.Max(8, context.Width - 4);

        /// <summary>
        /// The message shown when a call did not succeed. Denied and cancelled calls are stated
        /// as such rather than as generic failures (#264) - today they are indistinguishable.
        /// </summary>
        public static string FailureText(ToolCallSnapshot snapshot)
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

        /// <summary>Failure rows, colored with the theme's error role.</summary>
        public static IReadOnlyList<StyledLine> ErrorBodyFor(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var text = FailureText(snapshot);
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<StyledLine>();

            var rows = ToolOutputFormatter.Format(text, BodyWidth(context), context.RowBudget, context.Theme).Rows;
            // A tool's own error text has no ANSI styling of its own to preserve, so the whole
            // row takes the error color.
            return rows.Select(r => StyledLine.Plain(r.Text, context.Theme.Error)).ToList();
        }

        /// <summary>Duration for a completed header, once it is long enough to be information.</summary>
        public static string? CompletedTrailing(ToolCallSnapshot snapshot)
            => snapshot.Duration is { } d && d >= ToolOutputFormatter.MinimumReportedDuration
                ? ToolOutputFormatter.FormatDuration(d)
                : null;

        /// <summary>Live elapsed clock for a call still in flight.</summary>
        public static string RunningTrailing(ToolCallSnapshot snapshot)
            => ToolOutputFormatter.FormatDuration(DateTime.UtcNow - snapshot.StartedAtUtc);

        /// <summary>
        /// Render a payload for display. Strings pass through; anything structured is serialized
        /// with indentation so it is readable instead of appearing as one truncated line.
        /// </summary>
        public static string? AsText(object? data)
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
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// "[key=value, key=value]" over the primitive arguments, matching opencode's generic row.
        /// Structured arguments are summarized by shape rather than dropped silently, so the user
        /// can still tell that a list of twelve things was passed.
        /// </summary>
        public static string FormatArguments(IReadOnlyDictionary<string, object?> parameters)
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

            int fields = value switch
            {
                IReadOnlyDictionary<string, object?> ro => ro.Count,
                IDictionary<string, object?> d => d.Count,
                _ => value.GetType().GetProperties().Length
            };
            return fields > 0 ? $"{{{ToolOutputFormatter.Pluralize(fields, "field")}}}" : "{}";
        }
    }
}
