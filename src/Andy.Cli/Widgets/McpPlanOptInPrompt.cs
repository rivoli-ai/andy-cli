using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Modes;
using Andy.Cli.Themes;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// The interactive Plan-mode opt-in offer shown when an MCP server connects (issue #278).
    ///
    /// Plan mode denies every tool it cannot verify as read-only, and MCP tools carry no capability
    /// metadata, so they are denied by default. Rather than leaving the user to discover that at the
    /// moment a plan turn fails, this overlay surfaces the server's tool list at connection time and
    /// offers the choice up front.
    ///
    /// It NEVER grants anything on its own. Closing it, pressing Esc, or skipping records only that
    /// the offer was shown; the tools stay denied. Only the explicit "all" and "selected" actions
    /// write a grant.
    ///
    /// Offers are queued: one server is shown at a time and the next appears after a decision.
    /// </summary>
    public sealed class McpPlanOptInPrompt
    {
        /// <summary>One offer: a connected server and the tools Plan mode would currently deny.</summary>
        private sealed record Offer(string ServerName, IReadOnlyList<string> ToolIds);

        private readonly PlanModeGrantStore _grants;
        private readonly Queue<Offer> _queue = new();
        private readonly HashSet<int> _selected = new();
        private Offer? _current;
        private int _cursor;
        private string _status = string.Empty;

        public McpPlanOptInPrompt(PlanModeGrantStore grants)
        {
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        }

        /// <summary>True while an offer is on screen and owns the keyboard.</summary>
        public bool IsOpen => _current is not null;

        /// <summary>The server currently being offered, or null.</summary>
        public string? ServerName => _current?.ServerName;

        /// <summary>The tools currently being offered.</summary>
        public IReadOnlyList<string> Tools => _current?.ToolIds ?? Array.Empty<string>();

        /// <summary>Highlighted row (exposed for tests).</summary>
        public int CursorIndex => _cursor;

        /// <summary>Indices the user has ticked for a per-tool grant (exposed for tests).</summary>
        public IReadOnlyCollection<int> SelectedIndices => _selected;

        /// <summary>Number of offers still waiting behind the current one.</summary>
        public int PendingCount => _queue.Count;

        /// <summary>
        /// Queues an offer for a connected server if one is warranted: the server must expose at
        /// least one tool Plan mode would deny, and at least one of those must not have been offered
        /// before. Returns true when an offer was queued.
        /// </summary>
        public bool Enqueue(string serverName, IReadOnlyList<string> toolIds)
        {
            if (string.IsNullOrWhiteSpace(serverName) || toolIds is null || toolIds.Count == 0)
            {
                return false;
            }

            if (!_grants.NeedsOffer(serverName, toolIds))
            {
                return false;
            }

            var ungranted = _grants.UngrantedTools(toolIds);
            if (ungranted.Count == 0)
            {
                return false;
            }

            _queue.Enqueue(new Offer(serverName.Trim(), ungranted));
            if (_current is null)
            {
                Advance();
            }

            return true;
        }

        public void MoveSelection(int delta)
        {
            if (_current is null || _current.ToolIds.Count == 0)
            {
                return;
            }

            _cursor = Math.Clamp(_cursor + delta, 0, _current.ToolIds.Count - 1);
        }

        /// <summary>Ticks or unticks the highlighted tool for a per-tool grant.</summary>
        public void ToggleSelected()
        {
            if (_current is null || _current.ToolIds.Count == 0)
            {
                return;
            }

            if (!_selected.Add(_cursor))
            {
                _selected.Remove(_cursor);
            }
        }

        /// <summary>
        /// Grants every tool from this server, now and in future. Records the offer as answered and
        /// moves to the next queued server.
        /// </summary>
        public string GrantServerWide()
        {
            if (_current is null)
            {
                return string.Empty;
            }

            var offer = _current;
            var result = _grants.GrantServer(offer.ServerName);
            Finish(offer);
            return "[mode] " + result.Message;
        }

        /// <summary>
        /// Grants exactly the ticked tools. Unticked tools - and any tool this server exposes later -
        /// stay denied until opted in.
        /// </summary>
        public string GrantSelectedTools()
        {
            if (_current is null)
            {
                return string.Empty;
            }

            var offer = _current;
            var chosen = _selected
                .Where(i => i >= 0 && i < offer.ToolIds.Count)
                .OrderBy(i => i)
                .Select(i => offer.ToolIds[i])
                .ToArray();

            if (chosen.Length == 0)
            {
                _status = "Select at least one tool with Space, or press A for all / N to skip.";
                return string.Empty;
            }

            var result = _grants.GrantTools(chosen);
            Finish(offer);
            return "[mode] " + result.Message;
        }

        /// <summary>
        /// Declines the offer. Nothing is granted; the tools remain denied in Plan mode. The offer is
        /// recorded so it is not shown again unless the server exposes a new tool.
        /// </summary>
        public string Skip()
        {
            if (_current is null)
            {
                return string.Empty;
            }

            var offer = _current;
            Finish(offer);
            return $"[mode] MCP server '{offer.ServerName}' stays denied in Plan mode. "
                + $"Opt in later with '/mode allow-server {offer.ServerName}' or '/mode allow <tool-id>'.";
        }

        private void Finish(Offer offer)
        {
            // Recorded whatever the answer was: this is the "already asked" bookkeeping, not a grant.
            _grants.RecordOffered(offer.ServerName, offer.ToolIds);
            Advance();
        }

        private void Advance()
        {
            _selected.Clear();
            _cursor = 0;
            _status = string.Empty;
            _current = _queue.Count > 0 ? _queue.Dequeue() : null;
        }

        public void Render(L.Rect viewport, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int vw = (int)viewport.Width, vh = (int)viewport.Height;
            if (_current is null || vw < 32 || vh < 10)
            {
                return;
            }

            var offer = _current;
            var theme = Theme.Current;

            int width = Math.Min(vw - 4, 96);
            int x = (vw - width) / 2;
            const int headerRows = 3;
            const int footerRows = 3;
            int maxListRows = Math.Max(3, vh - 6 - headerRows - footerRows);
            int listRows = Math.Min(Math.Max(offer.ToolIds.Count, 1), maxListRows);
            int height = headerRows + listRows + footerRows;
            int y = Math.Max(1, (vh - height) / 3);

            var bg = new DL.Rgb24(22, 22, 30);
            var fg = theme.Text;
            var dim = new DL.Rgb24(140, 140, 150);
            var accent = theme.Accent;
            var selBg = new DL.Rgb24(45, 50, 70);

            b.PushClip(new DL.ClipPush(x, y, width, height));
            b.DrawRect(new DL.Rect(x, y, width, height, bg));

            b.DrawText(new DL.TextRun(x + 1, y, " Plan mode: MCP tool opt-in ", bg, accent, DL.CellAttrFlags.Bold));
            var queued = _queue.Count > 0 ? $" ({_queue.Count} more server{(_queue.Count == 1 ? "" : "s")})" : "";
            b.DrawText(new DL.TextRun(
                x + 2, y + 1,
                Truncate($"MCP server '{offer.ServerName}' connected with {offer.ToolIds.Count} tool(s).{queued}", width - 4),
                fg, bg, DL.CellAttrFlags.None));
            b.DrawText(new DL.TextRun(
                x + 2, y + 2,
                Truncate("Plan mode denies these until you opt in. Build mode is unaffected.", width - 4),
                dim, bg, DL.CellAttrFlags.None));

            int first = 0;
            if (offer.ToolIds.Count > listRows)
            {
                first = Math.Clamp(_cursor - listRows / 2, 0, offer.ToolIds.Count - listRows);
            }

            int row = y + headerRows;
            for (int i = first; i < first + listRows && i < offer.ToolIds.Count; i++)
            {
                bool isCursor = i == _cursor;
                var rowBg = isCursor ? selBg : bg;
                if (isCursor)
                {
                    b.DrawRect(new DL.Rect(x, row, width, 1, selBg));
                }

                b.DrawText(new DL.TextRun(x + 2, row, isCursor ? ">" : " ", accent, rowBg, DL.CellAttrFlags.Bold));
                b.DrawText(new DL.TextRun(
                    x + 4, row, _selected.Contains(i) ? "[x]" : "[ ]",
                    _selected.Contains(i) ? accent : dim, rowBg, DL.CellAttrFlags.None));
                b.DrawText(new DL.TextRun(
                    x + 8, row, Truncate(offer.ToolIds[i], width - 10), fg, rowBg, DL.CellAttrFlags.None));
                row++;
            }

            int footerY = y + height - footerRows;
            b.DrawText(new DL.TextRun(
                x + 2, footerY,
                "A allow ALL from this server (incl. future tools)", accent, bg, DL.CellAttrFlags.None));
            b.DrawText(new DL.TextRun(
                x + 2, footerY + 1,
                "Space select  Enter allow selected  N/Esc skip  up/down move", dim, bg, DL.CellAttrFlags.None));
            if (!string.IsNullOrEmpty(_status))
            {
                b.DrawText(new DL.TextRun(
                    x + 2, footerY + 2, Truncate(_status, width - 4), accent, bg, DL.CellAttrFlags.None));
            }

            b.Pop();
        }

        private static string Truncate(string s, int max)
        {
            if (max <= 1)
            {
                return string.Empty;
            }

            return s.Length <= max ? s : s.Substring(0, max - 1) + "...";
        }
    }
}
