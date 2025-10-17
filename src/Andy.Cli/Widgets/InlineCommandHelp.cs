using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Themes;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Inline command help widget that displays available slash commands
    /// as the user types. Shows up below the prompt area.
    /// </summary>
    public sealed class InlineCommandHelp
    {
        private readonly List<CommandInfo> _allCommands = new();
        private List<CommandInfo> _filteredCommands = new();
        private const int MaxDisplayLines = 5;

        public class CommandInfo
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string[] Aliases { get; set; } = Array.Empty<string>();
        }

        public void SetCommands(IEnumerable<CommandInfo> commands)
        {
            _allCommands.Clear();
            _allCommands.AddRange(commands);
        }

        public void UpdateFilter(string promptText)
        {
            // Only show help if prompt starts with /
            if (!promptText.StartsWith("/"))
            {
                _filteredCommands.Clear();
                return;
            }

            // Extract the command part (everything after / up to first space)
            string query = promptText.Substring(1).ToLowerInvariant();
            int spaceIndex = query.IndexOf(' ');
            if (spaceIndex > 0)
            {
                query = query.Substring(0, spaceIndex);
            }

            // Filter commands by name or alias
            if (string.IsNullOrEmpty(query))
            {
                _filteredCommands = _allCommands.Take(MaxDisplayLines).ToList();
            }
            else
            {
                _filteredCommands = _allCommands
                    .Where(c => c.Name.ToLowerInvariant().Contains(query) ||
                               c.Aliases.Any(a => a.ToLowerInvariant().Contains(query)))
                    .Take(MaxDisplayLines)
                    .ToList();
            }
        }

        public int GetHeight()
        {
            if (_filteredCommands.Count == 0) return 0;
            // Add 2 for top and bottom borders
            return Math.Min(_filteredCommands.Count + 2, MaxDisplayLines + 2);
        }

        public void Render(int x, int y, int width, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (_filteredCommands.Count == 0 || width <= 0) return;

            var theme = Theme.Current;
            int height = GetHeight();

            // Draw background and border
            b.PushClip(new DL.ClipPush(x, y, width, height));
            b.DrawRect(new DL.Rect(x, y, width, height, new DL.Rgb24(30, 30, 40)));
            b.DrawBorder(new DL.Border(x, y, width, height, "single", new DL.Rgb24(100, 150, 200)));

            // Render commands
            int availableWidth = Math.Max(1, width - 4); // Account for borders and padding
            int currentY = y + 1;

            foreach (var cmd in _filteredCommands.Take(height - 2))
            {
                if (currentY >= y + height - 1) break;

                // Format: /command - description
                string cmdText = $"/{cmd.Name}";
                string separator = " - ";
                int cmdLen = cmdText.Length + separator.Length;
                int descLen = Math.Max(0, availableWidth - cmdLen);

                // Truncate description if needed
                string desc = cmd.Description;
                if (desc.Length > descLen && descLen > 3)
                {
                    desc = desc.Substring(0, descLen - 3) + "...";
                }
                else if (desc.Length > descLen)
                {
                    desc = desc.Substring(0, descLen);
                }

                // Draw command name in accent color
                b.DrawText(new DL.TextRun(x + 2, currentY, cmdText, new DL.Rgb24(120, 200, 255), new DL.Rgb24(30, 30, 40), DL.CellAttrFlags.Bold));

                // Draw separator and description
                if (descLen > 0)
                {
                    b.DrawText(new DL.TextRun(x + 2 + cmdText.Length, currentY, separator, new DL.Rgb24(150, 150, 150), new DL.Rgb24(30, 30, 40), DL.CellAttrFlags.None));
                    b.DrawText(new DL.TextRun(x + 2 + cmdText.Length + separator.Length, currentY, desc, new DL.Rgb24(200, 200, 200), new DL.Rgb24(30, 30, 40), DL.CellAttrFlags.None));
                }

                currentY++;
            }

            b.Pop();
        }
    }
}
