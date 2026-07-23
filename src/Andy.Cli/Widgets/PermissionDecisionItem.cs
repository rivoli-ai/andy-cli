using System;
using System.Collections.Generic;
using Andy.Permissions.Model;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Transcript record of a permission decision (issue #224): after the user resolves the
    /// permission dialog, one of these is appended to the feed so there is a visible, auditable
    /// trace of what was approved or denied - the tool, the command(s)/resource(s) involved, and
    /// the choice that was made. Command lines reuse the same risk coloring as the dialog itself.
    /// </summary>
    public sealed class PermissionDecisionItem : IFeedItem
    {
        private readonly bool _approved;
        private readonly string _header;
        private readonly List<PermissionDialogLine> _detailLines;

        public PermissionDecisionItem(PermissionRequest request, PermissionDecision decision)
        {
            _approved = decision.Allowed;
            var tool = request.ToolDisplayName ?? request.ToolId;
            _header = $"{DecisionTag(decision)} {tool}";

            _detailLines = new List<PermissionDialogLine>();
            foreach (var command in PermissionDialogContent.AskedCommands(request))
            {
                var kind = CommandRiskClassifier.Classify(command) == CommandRiskLevel.Dangerous
                    ? PermissionLineKind.DangerousCommand
                    : PermissionLineKind.Command;
                _detailLines.Add(new PermissionDialogLine("$ " + command, kind));
            }

            // Non-command resources that forced the Ask (paths, hosts) are recorded too.
            foreach (var resource in request.Evaluation.Resources)
            {
                if (resource.Outcome == PermissionOutcome.Ask &&
                    resource.Access.Kind != ResourceKind.Command &&
                    !string.IsNullOrWhiteSpace(resource.Access.Value))
                {
                    var label = resource.Access.Kind.ToString().ToLowerInvariant();
                    _detailLines.Add(new PermissionDialogLine(
                        $"{label}: {resource.Access.Value}", PermissionLineKind.Summary));
                }
            }

            // A decision with no resource detail still records what was asked for.
            if (_detailLines.Count == 0 && !string.IsNullOrWhiteSpace(request.ActionSummary))
            {
                _detailLines.Add(new PermissionDialogLine(request.ActionSummary, PermissionLineKind.Summary));
            }
        }

        /// <summary>The header line, e.g. "[approved once] Execute Command" (exposed for tests).</summary>
        internal string HeaderText => _header;

        private static string DecisionTag(PermissionDecision decision)
        {
            if (!decision.Allowed)
            {
                return "[denied]";
            }

            return decision.Persist switch
            {
                PersistScope.Session => "[approved for session]",
                PersistScope.Once => "[approved once]",
                _ => $"[approved ({decision.Persist.ToString().ToLowerInvariant()})]",
            };
        }

        private List<PermissionDialogLine> WrapAll(int width)
        {
            var wrapped = new List<PermissionDialogLine>();
            foreach (var line in TextWrap.Wrap(_header, Math.Max(1, width)))
            {
                wrapped.Add(new PermissionDialogLine(line, PermissionLineKind.Summary));
            }

            int indentWidth = Math.Max(1, width - 2);
            foreach (var detail in _detailLines)
            {
                foreach (var line in TextWrap.Wrap(detail.Text, indentWidth))
                {
                    wrapped.Add(new PermissionDialogLine("  " + line, detail.Kind));
                }
            }

            return wrapped;
        }

        /// <inheritdoc />
        public int MeasureLineCount(int width) => WrapAll(width).Count;

        /// <inheritdoc />
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            var theme = Themes.Theme.Current;
            var wrapped = WrapAll(width);
            int headerRows = TextWrap.Wrap(_header, Math.Max(1, width)).Count;
            var headerColor = _approved ? theme.Success : theme.Error;

            int row = 0;
            for (int i = startLine; i < wrapped.Count && row < maxLines; i++, row++)
            {
                var line = wrapped[i];
                if (line.Text.Length == 0)
                {
                    continue;
                }

                DL.Rgb24 fg;
                DL.CellAttrFlags attrs = DL.CellAttrFlags.None;
                if (i < headerRows)
                {
                    fg = headerColor;
                    attrs = DL.CellAttrFlags.Bold;
                }
                else if (line.Kind == PermissionLineKind.Summary)
                {
                    fg = theme.TextDim;
                }
                else
                {
                    fg = PermissionDialogContent.ColorFor(line.Kind);
                }

                var text = line.Text.Length > width ? line.Text[..width] : line.Text;
                b.DrawText(new DL.TextRun(x, y + row, text, fg, theme.Background, attrs));
            }
        }
    }
}
