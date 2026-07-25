using System;
using System.Collections.Generic;
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
            var header = BuildHeader(snapshot, context);

            if (!snapshot.IsComplete)
                return ToolPresentation.Line(header, ToolPresenterHelpers.RunningTrailing(snapshot));

            var body = BuildBody(snapshot, context);
            return new ToolPresentation
            {
                Header = header,
                Trailing = ToolPresenterHelpers.CompletedTrailing(snapshot),
                Body = body,
                // A single line of result is a fact and stays inline; more than that is a
                // document and earns the block treatment.
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

            var arguments = ToolPresenterHelpers.FormatArguments(snapshot.Parameters);
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
            if (!snapshot.IsSuccessful)
                return ToolPresenterHelpers.ErrorBodyFor(snapshot, context);

            var text = ToolPresenterHelpers.AsText(snapshot.Data) ?? snapshot.Message;
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<StyledLine>();

            return ToolOutputFormatter.Format(text, ToolPresenterHelpers.BodyWidth(context),
                context.RowBudget, context.Theme).Rows;
        }
    }
}
