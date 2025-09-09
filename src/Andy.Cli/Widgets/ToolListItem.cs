using System;
using System.Collections.Generic;
using Andy.Tools.Core;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// A feed item that renders a list of tools with their status and permissions
    /// </summary>
    public sealed class ToolListItem : IFeedItem
    {
        private readonly List<ToolEntry> _entries = new();
        private readonly string _title;

        public class ToolEntry
        {
            public string? CategoryName { get; set; }
            public string? ToolName { get; set; }
            public string? ToolId { get; set; }
            public string? Description { get; set; }
            public bool IsEnabled { get; set; }
            public ToolPermissionFlags Permissions { get; set; }
        }

        public ToolListItem(string title = "")
        {
            _title = title;
        }

        public void AddCategory(string categoryName)
        {
            _entries.Add(new ToolEntry { CategoryName = categoryName });
        }

        public void AddTool(string toolName, string description, bool isEnabled, ToolPermissionFlags permissions, string? toolId = null)
        {
            _entries.Add(new ToolEntry
            {
                ToolName = toolName,
                Description = description,
                IsEnabled = isEnabled,
                Permissions = permissions,
                ToolId = toolId
            });
        }

        public int MeasureLineCount(int width)
        {
            int count = string.IsNullOrEmpty(_title) ? 0 : 1;
            foreach (var entry in _entries)
            {
                if (!string.IsNullOrEmpty(entry.CategoryName))
                    count++; // Category header line
                else if (!string.IsNullOrEmpty(entry.ToolName))
                {
                    count++; // Tool line
                    if (!string.IsNullOrEmpty(entry.Description))
                        count++; // Description line
                    if (entry.Permissions != ToolPermissionFlags.None)
                        count++; // Permissions line
                }
            }
            return Math.Max(1, count);
        }

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (width <= 0 || maxLines <= 0) return;

            var blackBg = new DL.Rgb24(0, 0, 0);
            var greenFg = new DL.Rgb24(0, 255, 0);
            var redFg = new DL.Rgb24(255, 80, 80);
            var whiteFg = new DL.Rgb24(220, 220, 220);
            var grayFg = new DL.Rgb24(150, 150, 150);
            var cyanFg = new DL.Rgb24(100, 200, 255);
            var yellowFg = new DL.Rgb24(255, 200, 0);

            int currentLine = 0;
            int renderedLines = 0;

            // Render title if present
            if (!string.IsNullOrEmpty(_title))
            {
                if (currentLine >= startLine && renderedLines < maxLines)
                {
                    b.DrawText(new DL.TextRun(x, y + renderedLines, _title, whiteFg, blackBg, DL.CellAttrFlags.Bold));
                    renderedLines++;
                }
                currentLine++;
            }

            // Render entries
            foreach (var entry in _entries)
            {
                if (renderedLines >= maxLines) break;

                if (!string.IsNullOrEmpty(entry.CategoryName))
                {
                    // Category header
                    if (currentLine >= startLine && renderedLines < maxLines)
                    {
                        b.DrawText(new DL.TextRun(x, y + renderedLines, entry.CategoryName + " Tools:", cyanFg, blackBg, DL.CellAttrFlags.Bold));
                        renderedLines++;
                    }
                    currentLine++;
                }
                else if (!string.IsNullOrEmpty(entry.ToolName))
                {
                    // Tool entry with status
                    if (currentLine >= startLine && renderedLines < maxLines)
                    {
                        string status = entry.IsEnabled ? "OK" : "X";
                        var statusColor = entry.IsEnabled ? greenFg : redFg;

                        // Render status indicator with brackets
                        int pos = x + 2; // Indent tools
                        b.DrawText(new DL.TextRun(pos, y + renderedLines, "[", whiteFg, blackBg, DL.CellAttrFlags.None));
                        pos += 1;
                        b.DrawText(new DL.TextRun(pos, y + renderedLines, status, statusColor, blackBg, DL.CellAttrFlags.None));
                        pos += status.Length;
                        b.DrawText(new DL.TextRun(pos, y + renderedLines, "] ", whiteFg, blackBg, DL.CellAttrFlags.None));
                        pos += 2;

                        // Render tool name
                        b.DrawText(new DL.TextRun(pos, y + renderedLines, entry.ToolName, whiteFg, blackBg, DL.CellAttrFlags.Bold));
                        pos += entry.ToolName?.Length ?? 0;

                        // Render tool ID if present
                        if (!string.IsNullOrEmpty(entry.ToolId))
                        {
                            var idText = $" (ID: {entry.ToolId})";
                            b.DrawText(new DL.TextRun(pos, y + renderedLines, idText, grayFg, blackBg, DL.CellAttrFlags.None));
                        }

                        renderedLines++;
                    }
                    currentLine++;

                    // Render description if present
                    if (!string.IsNullOrEmpty(entry.Description))
                    {
                        if (currentLine >= startLine && renderedLines < maxLines)
                        {
                            b.DrawText(new DL.TextRun(x + 7, y + renderedLines, entry.Description, grayFg, blackBg, DL.CellAttrFlags.None));
                            renderedLines++;
                        }
                        currentLine++;
                    }

                    // Render permissions if not None
                    if (entry.Permissions != ToolPermissionFlags.None)
                    {
                        if (currentLine >= startLine && renderedLines < maxLines)
                        {
                            var permStr = $"Requires: {entry.Permissions}";
                            b.DrawText(new DL.TextRun(x + 7, y + renderedLines, permStr, yellowFg, blackBg, DL.CellAttrFlags.None));
                            renderedLines++;
                        }
                        currentLine++;
                    }
                }
            }
        }
    }
}