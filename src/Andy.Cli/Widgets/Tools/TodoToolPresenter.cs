using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>Where a todo stands, as reported by the tool.</summary>
    public enum TodoState
    {
        /// <summary>Not started.</summary>
        Pending,

        /// <summary>Being worked on now.</summary>
        InProgress,

        /// <summary>Finished.</summary>
        Completed,

        /// <summary>Waiting on something else.</summary>
        Blocked,

        /// <summary>Abandoned.</summary>
        Cancelled
    }

    /// <summary>One todo, read from the objects todo_management returns.</summary>
    /// <param name="Text">What is to be done.</param>
    /// <param name="State">Its status.</param>
    /// <param name="CurrentAction">What is happening on it right now, when the tool says.</param>
    public sealed record TodoEntry(string Text, TodoState State, string? CurrentAction)
    {
        /// <summary>Read the todo list off a completed snapshot.</summary>
        public static IReadOnlyList<TodoEntry> From(ToolCallSnapshot snapshot)
        {
            var entries = new List<TodoEntry>();
            foreach (var item in snapshot.ResultList("todos", "items"))
            {
                if (item is null) continue;
                var text = ToolData.GetString(item, "text", "content", "title");
                if (text is null) continue;

                entries.Add(new TodoEntry(
                    text,
                    Parse(ToolData.GetString(item, "status", "state")),
                    ToolData.GetString(item, "current_action")));
            }
            return entries;
        }

        private static TodoState Parse(string? status) => (status ?? "").ToLowerInvariant() switch
        {
            "inprogress" or "in_progress" or "active" => TodoState.InProgress,
            "completed" or "done" => TodoState.Completed,
            "blocked" => TodoState.Blocked,
            "cancelled" or "canceled" => TodoState.Cancelled,
            _ => TodoState.Pending
        };
    }

    /// <summary>
    /// Renders the todo list (issue #258).
    ///
    /// This is the tool that tells the user what the agent plans to do, and it was rendered as a
    /// generic tool call: a "Updating todo list" header and the first line of the raw result. No
    /// checklist widget existed anywhere in the feed, so the plan itself - the thing worth showing
    /// - never appeared.
    ///
    /// Statuses are styled rather than spelled out, so the current focus is readable at a glance:
    /// the in-progress item is the accent color and bold, done items are dim, and blocked items
    /// take the warning color.
    /// </summary>
    public sealed class TodoToolPresenter : IToolPresenter
    {
        /// <inheritdoc />
        public bool CanPresent(string toolName) => toolName is "todo_management";

        /// <inheritdoc />
        public ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context)
        {
            var theme = context.Theme;
            var todos = snapshot.IsComplete ? TodoEntry.From(snapshot) : Array.Empty<TodoEntry>();
            bool reading = IsRead(snapshot);

            var header = StyledLine.Plain(
                !snapshot.IsComplete ? (reading ? "Reading todo list" : "Updating plan")
                : reading ? "Todo list" : "Plan",
                theme.ToolName, DL.CellAttrFlags.Bold);

            if (!snapshot.IsComplete) return ToolPresentation.Line(header);

            if (!snapshot.IsSuccessful)
                return new ToolPresentation { Header = header, Body = ToolPresenterHelpers.ErrorBodyFor(snapshot, context) };

            var trailing = BuildTrailing(todos, snapshot);

            // A read is a fact ("5 items, 2 done"); only a write is worth the whole checklist.
            // A superseded plan collapses to its header too, so a long session is not dominated by
            // every revision of the plan while the history of it is still there.
            if (todos.Count == 0 || reading || snapshot.IsSuperseded)
                return ToolPresentation.Line(header, trailing);

            return new ToolPresentation
            {
                Header = header,
                Trailing = trailing,
                Body = todos.Select(t => RenderTodo(t, theme)).ToList(),
                Layout = ToolLayout.Block
            };
        }

        // The list-style actions read; everything else changes the plan.
        private static bool IsRead(ToolCallSnapshot snapshot)
        {
            var action = (snapshot.Argument("action", "operation") ?? "").ToLowerInvariant();
            return action.Contains("list") || action.Contains("get");
        }

        // "2/5 done" is the one number that says how far along the plan is.
        private static string? BuildTrailing(IReadOnlyList<TodoEntry> todos, ToolCallSnapshot snapshot)
        {
            if (todos.Count == 0)
            {
                var count = snapshot.ResultInt("count");
                return count is > 0 ? ToolOutputFormatter.Pluralize(count.Value, "item") : null;
            }

            int done = todos.Count(t => t.State is TodoState.Completed or TodoState.Cancelled);
            var text = $"{done}/{todos.Count} done";
            return snapshot.IsSuperseded ? text + ", superseded" : text;
        }

        private static StyledLine RenderTodo(TodoEntry todo, Themes.Theme theme)
        {
            // ASCII markers, per the project's terminal-UI convention.
            var (marker, color, attributes) = todo.State switch
            {
                TodoState.Completed => ("[x] ", theme.Ghost, DL.CellAttrFlags.None),
                TodoState.InProgress => ("[>] ", theme.Accent, DL.CellAttrFlags.Bold),
                TodoState.Blocked => ("[!] ", theme.Warning, DL.CellAttrFlags.None),
                TodoState.Cancelled => ("[-] ", theme.Ghost, DL.CellAttrFlags.Italic),
                _ => ("[ ] ", theme.ToolResult, DL.CellAttrFlags.None)
            };

            var spans = new List<StyledSpan>
            {
                new(marker, color, DL.CellAttrFlags.None),
                new(todo.Text, color, attributes)
            };

            if (!string.IsNullOrWhiteSpace(todo.CurrentAction) && todo.State == TodoState.InProgress)
                spans.Add(new StyledSpan("  " + todo.CurrentAction, theme.TextDim, DL.CellAttrFlags.Italic));

            return new StyledLine(spans);
        }
    }
}
