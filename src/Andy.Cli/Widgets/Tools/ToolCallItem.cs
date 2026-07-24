using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.ToolResults;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// The feed item every tool call renders through (issue #249).
    ///
    /// It owns the shared chrome - status glyph column, header wrapping, body gutter, block
    /// border, spinner and elapsed clock - and delegates WHAT to say to an
    /// <see cref="IToolPresenter"/>. Keeping the drawing in one class is what makes the
    /// <see cref="IFeedItem"/> contract safe: <see cref="MeasureLineCount"/> and
    /// <see cref="RenderSlice"/> both walk the same cached row plan, so the rows reserved always
    /// equal the rows drawn. A divergence there is what produces phantom blank lines in the feed.
    ///
    /// All colors come from the active theme; nothing here hardcodes an RGB value.
    /// </summary>
    public sealed class ToolCallItem : IFeedItem
    {
        // Braille spinner, matching the one the processing indicator already uses.
        private static readonly string[] SpinnerFrames =
            { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

        /// <summary>Columns the status glyph column occupies, including its trailing space.</summary>
        public const int GlyphWidth = 2;

        /// <summary>Indent applied to wrapped header continuation rows.</summary>
        private const string HeaderIndent = "  ";

        /// <summary>Gutter on the first body row of an inline item.</summary>
        private const string BodyGutterFirst = "  L ";

        /// <summary>Gutter on subsequent body rows, aligning under the first.</summary>
        private const string BodyGutterRest = "    ";

        /// <summary>Left border drawn down a block item's body.</summary>
        private const string BlockGutter = "  | ";

        private enum RowKind { Header, HeaderContinuation, Body }

        private readonly object _lock = new();
        private ToolCallSnapshot _snapshot;
        private readonly IToolPresenter _presenter;

        // Cached row plan; rebuilt when the width, the expand mode, or the snapshot changes.
        private List<(StyledLine Line, RowKind Kind)> _plan = new();
        private ToolPresentation? _presentation;
        private int _planWidth = -1;
        private bool _planExpanded;
        private ToolCallSnapshot? _planSnapshot;

        /// <summary>Create an item for a call that may still be running.</summary>
        public ToolCallItem(ToolCallSnapshot snapshot, IToolPresenter presenter)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        }

        /// <summary>The call this item is showing.</summary>
        public ToolCallSnapshot Snapshot
        {
            get { lock (_lock) return _snapshot; }
        }

        /// <summary>UI execution id, used to route updates to the right item.</summary>
        public string ToolId => Snapshot.ToolId;

        /// <summary>
        /// Replace the snapshot - when arguments arrive, when output streams in, when the call
        /// completes. The plan is invalidated so the next frame re-presents the call.
        /// </summary>
        public void Update(ToolCallSnapshot snapshot)
        {
            if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
            lock (_lock)
            {
                _snapshot = snapshot;
                _planSnapshot = null;
            }
        }

        /// <summary>Mutate the current snapshot in place (a convenience over read-modify-Update).</summary>
        public void Update(Func<ToolCallSnapshot, ToolCallSnapshot> mutate)
        {
            if (mutate is null) throw new ArgumentNullException(nameof(mutate));
            lock (_lock)
            {
                _snapshot = mutate(_snapshot);
                _planSnapshot = null;
            }
        }

        /// <inheritdoc />
        public int MeasureLineCount(int width)
        {
            if (width <= 0) return 1;
            lock (_lock)
            {
                EnsurePlan(width);
                return Math.Max(1, _plan.Count);
            }
        }

        /// <inheritdoc />
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines,
            DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (width <= 0 || maxLines <= 0) return;

            lock (_lock)
            {
                EnsurePlan(width);
                var theme = Themes.Theme.Current;
                var snapshot = _snapshot;

                int drawn = 0;
                for (int i = startLine; i < _plan.Count && drawn < maxLines; i++)
                {
                    int row = y + drawn;
                    var (line, kind) = _plan[i];

                    if (i == 0)
                    {
                        // The header is composed live so the spinner and elapsed clock animate
                        // without changing the row count.
                        DrawHeaderRow(b, x, row, width, snapshot, theme, line);
                    }
                    else
                    {
                        line.Render(b, x, row, width, kind == RowKind.Body ? theme.ToolResult : theme.Text);
                    }
                    drawn++;
                }
            }
        }

        private void DrawHeaderRow(DL.DisplayListBuilder b, int x, int y, int width,
            ToolCallSnapshot snapshot, Themes.Theme theme, StyledLine headerRow)
        {
            var (glyph, color) = StatusGlyph(snapshot, theme);
            b.DrawText(new DL.TextRun(x, y, glyph, color, null,
                snapshot.IsComplete ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));

            int bodyX = x + GlyphWidth;
            int bodyWidth = width - GlyphWidth;
            if (bodyWidth <= 0) return;

            headerRow.Render(b, bodyX, y, bodyWidth, theme.ToolName);
        }

        /// <summary>
        /// Status marker for a call. Plain ASCII (a spinner while running), colored from the
        /// theme's status roles so a failure reads as a failure under every palette.
        /// </summary>
        private static (string Glyph, DL.Rgb24 Color) StatusGlyph(ToolCallSnapshot snapshot, Themes.Theme theme)
            => snapshot.Status switch
            {
                ToolCallStatus.Running => (SpinnerFrame(snapshot.StartedAtUtc), theme.ToolRunning),
                ToolCallStatus.Success => ("*", theme.Success),
                ToolCallStatus.Failed => ("x", theme.Error),
                ToolCallStatus.Cancelled => ("-", theme.Warning),
                ToolCallStatus.Denied => ("-", theme.Warning),
                _ => ("*", theme.TextDim)
            };

        // Derived from wall-clock rather than a frame counter so the spinner turns at a constant
        // rate regardless of how often the feed happens to redraw.
        private static string SpinnerFrame(DateTime startedAtUtc)
        {
            var elapsed = DateTime.UtcNow - startedAtUtc;
            int frame = (int)(elapsed.TotalMilliseconds / 100) % SpinnerFrames.Length;
            return SpinnerFrames[Math.Max(0, frame)];
        }

        private void EnsurePlan(int width)
        {
            bool expanded = ToolOutputView.Expanded;
            if (width == _planWidth && expanded == _planExpanded
                && ReferenceEquals(_planSnapshot, _snapshot) && _plan.Count > 0)
                return;

            _planWidth = width;
            _planExpanded = expanded;
            _planSnapshot = _snapshot;

            var theme = Themes.Theme.Current;
            var context = new ToolPresentationContext(width, expanded, theme);
            _presentation = _presenter.Present(_snapshot, context);

            var plan = new List<(StyledLine, RowKind)>();

            // Header, wrapped into the space left by the glyph column.
            int headerWidth = Math.Max(1, width - GlyphWidth);
            var headerRows = AppendTrailing(_presentation.Header, _presentation.Trailing, headerWidth, theme)
                .Wrap(headerWidth)
                .ToList();

            for (int i = 0; i < headerRows.Count; i++)
            {
                plan.Add(i == 0
                    ? (headerRows[i], RowKind.Header)
                    : (headerRows[i].WithPrefix(StyledSpan.Plain(HeaderIndent)), RowKind.HeaderContinuation));
            }

            // Body, gutter-prefixed unless the presenter draws its own structure.
            var body = _presentation.Body;
            if (body.Count > 0)
            {
                if (!_presentation.IndentBody)
                {
                    foreach (var line in body) plan.Add((line, RowKind.Body));
                }
                else if (_presentation.Layout == ToolLayout.Block)
                {
                    var border = new StyledSpan(BlockGutter, theme.Border, DL.CellAttrFlags.None);
                    foreach (var line in body) plan.Add((line.WithPrefix(border), RowKind.Body));
                }
                else
                {
                    for (int i = 0; i < body.Count; i++)
                    {
                        var gutter = new StyledSpan(i == 0 ? BodyGutterFirst : BodyGutterRest,
                            theme.Ghost, DL.CellAttrFlags.None);
                        plan.Add((body[i].WithPrefix(gutter), RowKind.Body));
                    }
                }
            }

            if (plan.Count == 0) plan.Add((StyledLine.Empty, RowKind.Header));
            _plan = plan;
        }

        // The trailing metric rides on the header when there is room for it. It is dropped rather
        // than wrapped: a duration on a line of its own reads as content, which it is not.
        private static StyledLine AppendTrailing(StyledLine header, string? trailing, int width, Themes.Theme theme)
        {
            if (string.IsNullOrEmpty(trailing)) return header;
            string suffix = "  " + trailing;
            if (header.Width + suffix.Length > width) return header;
            return header.WithSuffix(new StyledSpan(suffix, theme.TextDim, DL.CellAttrFlags.None));
        }

        /// <summary>
        /// The rows this item would draw at <paramref name="width"/>, as plain text. Exposed for
        /// tests so a presenter's output can be asserted without standing up a display list.
        /// </summary>
        public IReadOnlyList<string> DebugRows(int width)
        {
            lock (_lock)
            {
                EnsurePlan(width);
                var rows = new List<string>();
                var theme = Themes.Theme.Current;
                for (int i = 0; i < _plan.Count; i++)
                {
                    var text = _plan[i].Line.Text;
                    rows.Add(i == 0 ? StatusGlyph(_snapshot, theme).Glyph + " " + text : text);
                }
                return rows;
            }
        }
    }
}
