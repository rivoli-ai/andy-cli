using System;
using System.Collections.Generic;
using Andy.Cli.Themes;
using Andy.Permissions.Model;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Inline tool-approval prompt (issue #222). While a permission request is pending, this widget
    /// takes over the prompt area instead of covering the transcript with a centered modal, so the
    /// conversation above stays fully visible and scrollable while the user decides. Text input is
    /// suspended for the duration and restored once a decision is made.
    ///
    /// Keyboard semantics match the old dialog: Left/Right/Tab move between
    /// "Allow once" / "Allow (session)" / "Deny" (Deny preselected as the safe default),
    /// Enter/Space confirm the highlighted choice, and Esc/D/N deny immediately. Up/Down scroll the
    /// request body when it overflows the capped panel height; PageUp/PageDown are intentionally
    /// NOT handled here so they keep scrolling the transcript.
    /// </summary>
    public sealed class InlineApprovalPrompt
    {
        /// <summary>The choice labels, in selection order.</summary>
        public static readonly IReadOnlyList<string> OptionLabels = new[] { "Allow once", "Allow (session)", "Deny" };

        private const int AllowOnceIndex = 0;
        private const int AllowSessionIndex = 1;
        private const int DenyIndex = 2;

        /// <summary>
        /// Non-body rows of the panel: top border w/ title (1) + tool line (1) + options row (1)
        /// + bottom border w/ hints (1).
        /// </summary>
        internal const int ChromeRows = 4;

        private PermissionRequest? _request;
        private int _selected = DenyIndex;
        private int _scroll;
        private int _maxScroll;

        /// <summary>True while a request is awaiting a decision (prompt input is suspended).</summary>
        public bool IsActive => _request is not null;

        /// <summary>Currently highlighted option index (exposed for tests).</summary>
        public int SelectedIndex => _selected;

        /// <summary>Current body scroll offset (exposed for tests).</summary>
        public int ScrollOffset => _scroll;

        /// <summary>Start showing a request. Deny is preselected as the safe default.</summary>
        public void Begin(PermissionRequest request)
        {
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _selected = DenyIndex;
            _scroll = 0;
            _maxScroll = 0;
        }

        /// <summary>
        /// Clear the pending request without producing a decision (e.g. the awaiting side was
        /// cancelled and already resolved itself to a deny).
        /// </summary>
        public void Dismiss()
        {
            _request = null;
            _selected = DenyIndex;
            _scroll = 0;
            _maxScroll = 0;
        }

        /// <summary>Inner text width for a given panel width (2 border columns + 1 pad each side).</summary>
        internal static int InnerWidth(int width) => Math.Max(8, width - 4);

        /// <summary>
        /// Panel height needed to show the whole request body, capped at <paramref name="maxHeight"/>
        /// (the body scrolls when capped). Returns 0 when no request is pending.
        /// </summary>
        public int GetDesiredHeight(int width, int maxHeight)
        {
            if (_request is null)
            {
                return 0;
            }

            int bodyCount = PermissionDialogContent.BuildBodyLines(_request, InnerWidth(width)).Count;
            int min = ChromeRows + 1; // always at least one body row
            return Math.Clamp(bodyCount + ChromeRows, min, Math.Max(min, maxHeight));
        }

        /// <summary>
        /// Handle one key while active. Returns the decision when the key resolves the request
        /// (the widget deactivates itself) and null otherwise. Every key is consumed: while an
        /// approval is pending no key reaches the text prompt.
        /// </summary>
        public PermissionDecision? HandleKey(ConsoleKeyInfo k)
        {
            if (_request is null)
            {
                return null;
            }

            if (k.Key == ConsoleKey.LeftArrow)
            {
                _selected = (_selected + OptionLabels.Count - 1) % OptionLabels.Count;
                return null;
            }

            if (k.Key == ConsoleKey.RightArrow || k.Key == ConsoleKey.Tab)
            {
                _selected = (_selected + 1) % OptionLabels.Count;
                return null;
            }

            if (k.Key == ConsoleKey.UpArrow)
            {
                _scroll = Math.Max(0, _scroll - 1);
                return null;
            }

            if (k.Key == ConsoleKey.DownArrow)
            {
                _scroll = Math.Min(_maxScroll, _scroll + 1);
                return null;
            }

            if (k.Key == ConsoleKey.Escape || k.Key == ConsoleKey.D || k.Key == ConsoleKey.N)
            {
                return Resolve(DenyIndex);
            }

            if (k.Key == ConsoleKey.Enter || k.Key == ConsoleKey.Spacebar)
            {
                return Resolve(_selected);
            }

            return null; // swallow everything else; typing is suspended
        }

        private PermissionDecision Resolve(int choice)
        {
            Dismiss();
            return choice switch
            {
                AllowOnceIndex => new PermissionDecision(true, PersistScope.Once),
                AllowSessionIndex => new PermissionDecision(true, PersistScope.Session),
                _ => new PermissionDecision(false, PersistScope.Once),
            };
        }

        /// <summary>
        /// Render the panel into the prompt area rect. Body lines reuse the shared
        /// <see cref="PermissionDialogContent"/> wrapping and risk coloring so the inline prompt
        /// shows exactly what the old dialog showed.
        /// </summary>
        public void Render(in L.Rect rect, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (_request is null)
            {
                return;
            }

            var theme = Theme.Current;
            int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;
            if (w < 8 || h < ChromeRows + 1)
            {
                return;
            }

            int innerW = InnerWidth(w);
            var body = PermissionDialogContent.BuildBodyLines(_request, innerW);
            int visible = Math.Min(body.Count, Math.Max(1, h - ChromeRows));
            _maxScroll = Math.Max(0, body.Count - visible);
            _scroll = Math.Clamp(_scroll, 0, _maxScroll);

            b.PushClip(new DL.ClipPush(x, y, w, h));
            b.DrawRect(new DL.Rect(x, y, w, h, theme.PromptBackground));
            b.DrawBorder(new DL.Border(x, y, w, h, "single", theme.Warning));

            // Title overlaid on the top border.
            string title = " Permission required ";
            b.DrawText(new DL.TextRun(x + 2, y, Fit(title, w - 4), theme.Warning, theme.PromptBackground, DL.CellAttrFlags.Bold));

            // Tool line, with a scroll position indicator when the body overflows.
            string tool = Fit($"Tool: {_request.ToolDisplayName ?? _request.ToolId}", innerW);
            b.DrawText(new DL.TextRun(x + 2, y + 1, tool, theme.Text, theme.PromptBackground, DL.CellAttrFlags.None));
            if (_maxScroll > 0)
            {
                string pos = $"[{_scroll + 1}-{_scroll + visible}/{body.Count}]";
                int posX = x + w - 2 - pos.Length;
                if (posX > x + 2 + tool.Length)
                {
                    b.DrawText(new DL.TextRun(posX, y + 1, pos, theme.TextDim, theme.PromptBackground, DL.CellAttrFlags.None));
                }
            }

            // Body: wrapped summary + color-coded "$ command" lines.
            for (int i = 0; i < visible; i++)
            {
                var line = body[_scroll + i];
                if (line.Text.Length == 0)
                {
                    continue;
                }

                var fg = PermissionDialogContent.ColorFor(line.Kind);
                var attrs = line.Kind == PermissionLineKind.DangerousCommand
                    ? DL.CellAttrFlags.Bold
                    : DL.CellAttrFlags.None;
                b.DrawText(new DL.TextRun(x + 2, y + 2 + i, Fit(line.Text, innerW), fg, theme.PromptBackground, attrs));
            }

            // Options row (same visual language as the old dialog buttons).
            int ox = x + 2;
            int optionsY = y + h - 2;
            for (int i = 0; i < OptionLabels.Count; i++)
            {
                bool on = i == _selected;
                var bg = on ? new DL.Rgb24(60, 90, 130) : new DL.Rgb24(40, 40, 50);
                var fg = on ? new DL.Rgb24(255, 255, 255) : new DL.Rgb24(180, 180, 180);
                string text = on ? $"[ {OptionLabels[i]} ]" : $"  {OptionLabels[i]}  ";
                b.DrawRect(new DL.Rect(ox, optionsY, text.Length, 1, bg));
                b.DrawText(new DL.TextRun(ox, optionsY, text, fg, bg, on ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));
                ox += text.Length + 2;
            }

            // Key hints overlaid on the bottom border (ASCII only).
            string hints = _maxScroll > 0
                ? " Left/Right select  Enter confirm  Esc deny  Up/Down scroll "
                : " Left/Right select  Enter confirm  Esc deny ";
            b.DrawText(new DL.TextRun(x + 2, y + h - 1, Fit(hints, w - 4), theme.TextDim, theme.PromptBackground, DL.CellAttrFlags.None));

            b.Pop();
        }

        private static string Fit(string text, int width)
        {
            if (width <= 0)
            {
                return string.Empty;
            }

            return text.Length <= width ? text : text[..width];
        }
    }
}
