using System;
using System.Collections.Generic;
using Andy.Cli.Services.ToolResults;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>How much visual weight a tool call gets in the feed.</summary>
    public enum ToolLayout
    {
        /// <summary>
        /// A one-line row plus optional short continuation lines. For tools whose result is a
        /// FACT: a file was read, a search found twelve matches, a directory holds 28 entries.
        /// </summary>
        Inline,

        /// <summary>
        /// A titled block set off from the surrounding feed. For tools whose result is a
        /// DOCUMENT: command output, a diff, a written file, a todo list.
        /// </summary>
        Block
    }

    /// <summary>Everything a presenter needs to lay itself out.</summary>
    /// <param name="Width">Columns available to the whole item, gutter included.</param>
    /// <param name="Expanded">True when the user has toggled full output with ctrl+o.</param>
    /// <param name="Theme">Active theme; presenters must take every color from it.</param>
    public sealed record ToolPresentationContext(int Width, bool Expanded, Themes.Theme Theme)
    {
        /// <summary>Row budget for output bodies under the current expand state.</summary>
        public int RowBudget => Expanded
            ? ToolOutputFormatter.ExpandedRowBudget
            : ToolOutputFormatter.CollapsedRowBudget;
    }

    /// <summary>
    /// What one tool call looks like: a header sentence, an optional trailing metric, and an
    /// optional body. Presenters return this; <see cref="ToolCallItem"/> owns all the drawing, so
    /// the measure/render line-count contract lives in exactly one place.
    /// </summary>
    public sealed record ToolPresentation
    {
        /// <summary>
        /// The header sentence, without the status glyph (the item draws that). Styled, so a
        /// presenter can syntax-highlight a command or color a path.
        /// </summary>
        public required StyledLine Header { get; init; }

        /// <summary>
        /// Short dim metric appended to the header row: a match count, a duration, "+18 -3".
        /// Dropped rather than wrapped when the terminal is too narrow for it.
        /// </summary>
        public string? Trailing { get; init; }

        /// <summary>Body rows, already wrapped to the width the presenter was given.</summary>
        public IReadOnlyList<StyledLine> Body { get; init; } = Array.Empty<StyledLine>();

        /// <summary>Inline row or block panel.</summary>
        public ToolLayout Layout { get; init; } = ToolLayout.Inline;

        /// <summary>
        /// Body rows are indented under the header with the "L" gutter. Presenters that render
        /// their own structure edge to edge (diffs, tables) set this to suppress it.
        /// </summary>
        public bool IndentBody { get; init; } = true;

        /// <summary>A bare inline row with no body.</summary>
        public static ToolPresentation Line(StyledLine header, string? trailing = null)
            => new() { Header = header, Trailing = trailing };
    }

    /// <summary>
    /// Turns one tool call into its feed presentation. One implementation per tool family
    /// (issue #249); <see cref="ToolPresenterRegistry"/> resolves them by tool id.
    ///
    /// Implementations must read the tool's STRUCTURED result off the snapshot
    /// (<see cref="ToolCallSnapshot.Data"/> / <see cref="ToolCallSnapshot.Metadata"/>) via
    /// <see cref="ToolData"/>. They must not parse rendered text, and they must take every color
    /// from <see cref="ToolPresentationContext.Theme"/>.
    /// </summary>
    public interface IToolPresenter
    {
        /// <summary>Normalized tool ids this presenter handles ("read_file", "execute_command", ...).</summary>
        bool CanPresent(string toolName);

        /// <summary>Lay out the call. Called on every frame, so it must be cheap and side-effect free.</summary>
        ToolPresentation Present(ToolCallSnapshot snapshot, ToolPresentationContext context);
    }
}
