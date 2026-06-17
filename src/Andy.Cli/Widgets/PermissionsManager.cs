using System;
using System.Collections.Generic;
using Andy.Cli.Services;
using Andy.Cli.Themes;
using Andy.Permissions.Model;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Interactive overlay for viewing and editing the file-backed permission layers
    /// (User / Project / Local). Navigate with the arrow keys; Enter/Space cycles a rule's outcome
    /// (Allow → Ask → Deny), Delete/'d' removes it, 'r' reloads. Every edit is written straight to
    /// the layer file it came from via <see cref="PermissionRuleFile"/>. Builtin/Session/Injected
    /// rules are not file-backed and are intentionally not shown here.
    /// </summary>
    public sealed class PermissionsManager
    {
        public sealed class Entry
        {
            public PermissionOutcome Outcome { get; set; }
            public string Rule { get; set; } = "";
            public PersistScope Scope { get; set; }
        }

        private static readonly PersistScope[] EditableScopes =
            { PersistScope.User, PersistScope.Project, PersistScope.Local };

        private readonly string _projectDirectory;
        private readonly List<Entry> _entries = new();
        private int _selected;
        private bool _isOpen;
        private string _status = "";

        public PermissionsManager(string projectDirectory)
        {
            _projectDirectory = projectDirectory;
        }

        public bool IsOpen => _isOpen;
        public int Count => _entries.Count;
        public int SelectedIndex => _selected;
        public IReadOnlyList<Entry> Entries => _entries;
        public Entry? Selected => _entries.Count > 0 ? _entries[_selected] : null;

        public void Open()
        {
            Reload();
            _selected = 0;
            _isOpen = true;
        }

        public void Close() => _isOpen = false;

        /// <summary>(Re)load all editable, file-backed rules from disk.</summary>
        public void Reload()
        {
            _entries.Clear();
            foreach (var scope in EditableScopes)
            {
                var file = PermissionRuleFile.Load(PermissionRuleFile.PathForScope(scope, _projectDirectory));
                foreach (var (outcome, rule) in file.Entries())
                    _entries.Add(new Entry { Outcome = outcome, Rule = rule, Scope = scope });
            }
            if (_selected >= _entries.Count) _selected = Math.Max(0, _entries.Count - 1);
            _status = _entries.Count == 0 ? "No editable rules. Add with: /permissions allow <tool>" : "";
        }

        public void MoveSelection(int delta)
        {
            if (_entries.Count == 0) return;
            _selected = Math.Clamp(_selected + delta, 0, _entries.Count - 1);
        }

        /// <summary>Cycle the selected rule Allow → Ask → Deny → Allow and persist the change.</summary>
        public void CycleSelectedOutcome()
        {
            var e = Selected;
            if (e == null) return;
            var next = e.Outcome switch
            {
                PermissionOutcome.Allow => PermissionOutcome.Ask,
                PermissionOutcome.Ask => PermissionOutcome.Deny,
                _ => PermissionOutcome.Allow,
            };
            var path = PermissionRuleFile.PathForScope(e.Scope, _projectDirectory);
            var file = PermissionRuleFile.Load(path);
            file.Set(e.Rule, next);
            file.Save(path);
            e.Outcome = next;
            _status = $"{e.Rule} -> {next} ({e.Scope})";
        }

        /// <summary>Remove the selected rule from its layer file.</summary>
        public void DeleteSelected()
        {
            var e = Selected;
            if (e == null) return;
            var path = PermissionRuleFile.PathForScope(e.Scope, _projectDirectory);
            var file = PermissionRuleFile.Load(path);
            file.Remove(e.Rule);
            file.Save(path);
            _entries.RemoveAt(_selected);
            if (_selected >= _entries.Count) _selected = Math.Max(0, _entries.Count - 1);
            _status = $"removed {e.Rule} ({e.Scope})";
        }

        public void Render(L.Rect viewport, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            int vw = (int)viewport.Width, vh = (int)viewport.Height;
            if (!_isOpen || vw < 24 || vh < 8) return;
            var theme = Theme.Current;

            int width = Math.Min(vw - 4, 96);
            int x = (vw - width) / 2;
            int headerRows = 2, footerRows = 2;
            int maxListRows = Math.Max(3, vh - 6 - headerRows - footerRows);
            int listRows = Math.Min(Math.Max(_entries.Count, 1), maxListRows);
            int height = headerRows + listRows + footerRows;
            int y = Math.Max(1, (vh - height) / 3);

            var bg = new DL.Rgb24(22, 22, 30);
            var fg = theme.Text;
            var dim = new DL.Rgb24(140, 140, 150);
            var accent = theme.Accent;
            var selBg = new DL.Rgb24(45, 50, 70);

            b.PushClip(new DL.ClipPush(x, y, width, height));
            b.DrawRect(new DL.Rect(x, y, width, height, bg));

            // Title
            b.DrawText(new DL.TextRun(x + 1, y, " Permissions Manager ", bg, accent, DL.CellAttrFlags.Bold));
            b.DrawText(new DL.TextRun(x + 24, y, "(file-backed layers only)", dim, bg, DL.CellAttrFlags.None));

            // Scroll window around the selection.
            int first = 0;
            if (_entries.Count > listRows)
                first = Math.Clamp(_selected - listRows / 2, 0, _entries.Count - listRows);

            int row = y + headerRows;
            if (_entries.Count == 0)
            {
                b.DrawText(new DL.TextRun(x + 2, row, "No editable rules.", dim, bg, DL.CellAttrFlags.None));
            }
            for (int i = first; i < first + listRows && i < _entries.Count; i++)
            {
                var e = _entries[i];
                bool sel = i == _selected;
                var rowBg = sel ? selBg : bg;
                if (sel) b.DrawRect(new DL.Rect(x, row, width, 1, selBg));
                var oc = OutcomeColor(e.Outcome, accent);
                b.DrawText(new DL.TextRun(x + 2, row, sel ? ">" : " ", accent, rowBg, DL.CellAttrFlags.Bold));
                b.DrawText(new DL.TextRun(x + 4, row, e.Outcome.ToString().PadRight(5), oc, rowBg, DL.CellAttrFlags.Bold));
                b.DrawText(new DL.TextRun(x + 10, row, Truncate(e.Rule, width - 24), fg, rowBg, DL.CellAttrFlags.None));
                b.DrawText(new DL.TextRun(x + width - 11, row, $"[{e.Scope}]".PadLeft(10), dim, rowBg, DL.CellAttrFlags.None));
                row++;
            }

            // Footer: key hints + status.
            int footerY = y + height - footerRows;
            b.DrawText(new DL.TextRun(x + 2, footerY,
                "up/down move  Enter/Space cycle  d delete  r reload  Esc close", dim, bg, DL.CellAttrFlags.None));
            if (!string.IsNullOrEmpty(_status))
                b.DrawText(new DL.TextRun(x + 2, footerY + 1, Truncate(_status, width - 4), accent, bg, DL.CellAttrFlags.None));

            b.Pop();
        }

        private static DL.Rgb24 OutcomeColor(PermissionOutcome o, DL.Rgb24 accent) => o switch
        {
            PermissionOutcome.Allow => new DL.Rgb24(100, 200, 120),
            PermissionOutcome.Ask => new DL.Rgb24(200, 180, 80),
            _ => new DL.Rgb24(210, 90, 90),
        };

        private static string Truncate(string s, int max)
        {
            if (max <= 1) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
