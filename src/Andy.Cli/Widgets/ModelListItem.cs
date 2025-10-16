using System;
using System.Collections.Generic;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// A feed item that renders model list with colored status indicators
    /// </summary>
    public sealed class ModelListItem : IFeedItem
    {
        private readonly List<ModelEntry> _entries = new();
        private readonly string _title;

        public class ModelEntry
        {
            public string Provider { get; set; } = "";
            public string ModelName { get; set; } = "";
            public string Description { get; set; } = "";
            public bool Available { get; set; }
            public bool IsCurrent { get; set; }
        }

        public ModelListItem(string title = "")
        {
            _title = title;
        }

        public void AddProvider(string providerName)
        {
            _entries.Add(new ModelEntry { Provider = providerName });
        }

        public void AddModel(string modelName, string description, bool available, bool isCurrent = false)
        {
            _entries.Add(new ModelEntry
            {
                ModelName = modelName,
                Description = description,
                Available = available,
                IsCurrent = isCurrent
            });
        }

        public void AddApiKeyStatus(string text)
        {
            _entries.Add(new ModelEntry { Description = text });
        }

        public int MeasureLineCount(int width)
        {
            int count = string.IsNullOrEmpty(_title) ? 0 : 1;
            foreach (var entry in _entries)
            {
                if (!string.IsNullOrEmpty(entry.Provider))
                    count++; // Provider name line
                else if (!string.IsNullOrEmpty(entry.ModelName))
                {
                    count++; // Model line
                    if (!string.IsNullOrEmpty(entry.Description))
                        count++; // Description line
                }
                else if (!string.IsNullOrEmpty(entry.Description))
                    count++; // API key status or other text
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

                if (!string.IsNullOrEmpty(entry.Provider))
                {
                    // Provider header
                    if (currentLine >= startLine && renderedLines < maxLines)
                    {
                        b.DrawText(new DL.TextRun(x, y + renderedLines, entry.Provider + ":", cyanFg, blackBg, DL.CellAttrFlags.None));
                        renderedLines++;
                    }
                    currentLine++;
                }
                else if (!string.IsNullOrEmpty(entry.ModelName))
                {
                    // Model entry with status
                    if (currentLine >= startLine && renderedLines < maxLines)
                    {
                        string indicator = entry.IsCurrent ? "→ " : "  ";
                        string status = entry.Available ? "OK" : "X";
                        var statusColor = entry.Available ? greenFg : redFg;
                        string availability = entry.Available ? "" : " (API key required)";

                        // Render indicator
                        int pos = x;
                        b.DrawText(new DL.TextRun(pos, y + renderedLines, indicator, whiteFg, blackBg, DL.CellAttrFlags.None));
                        pos += indicator.Length;

                        // Render colored status with brackets
                        b.DrawText(new DL.TextRun(pos, y + renderedLines, "[", whiteFg, blackBg, DL.CellAttrFlags.None));
                        pos += 1;
                        b.DrawText(new DL.TextRun(pos, y + renderedLines, status, statusColor, blackBg, DL.CellAttrFlags.None));
                        pos += status.Length;
                        b.DrawText(new DL.TextRun(pos, y + renderedLines, "] ", whiteFg, blackBg, DL.CellAttrFlags.None));
                        pos += 2;

                        // Render model name and availability
                        b.DrawText(new DL.TextRun(pos, y + renderedLines, entry.ModelName + availability, whiteFg, blackBg, DL.CellAttrFlags.None));

                        renderedLines++;
                    }
                    currentLine++;

                    // Render description if present
                    if (!string.IsNullOrEmpty(entry.Description))
                    {
                        if (currentLine >= startLine && renderedLines < maxLines)
                        {
                            b.DrawText(new DL.TextRun(x + 5, y + renderedLines, entry.Description, grayFg, blackBg, DL.CellAttrFlags.None));
                            renderedLines++;
                        }
                        currentLine++;
                    }
                }
                else if (!string.IsNullOrEmpty(entry.Description))
                {
                    // API key status or other text
                    if (currentLine >= startLine && renderedLines < maxLines)
                    {
                        // Check if this is an API key status line
                        if (entry.Description.Contains("[SET]"))
                        {
                            var parts = entry.Description.Split("[SET]");
                            if (parts.Length == 2)
                            {
                                int pos = x;
                                b.DrawText(new DL.TextRun(pos, y + renderedLines, parts[0], whiteFg, blackBg, DL.CellAttrFlags.None));
                                pos += parts[0].Length;
                                b.DrawText(new DL.TextRun(pos, y + renderedLines, "[", whiteFg, blackBg, DL.CellAttrFlags.None));
                                pos += 1;
                                b.DrawText(new DL.TextRun(pos, y + renderedLines, "SET", greenFg, blackBg, DL.CellAttrFlags.None));
                                pos += 3;
                                b.DrawText(new DL.TextRun(pos, y + renderedLines, "]", whiteFg, blackBg, DL.CellAttrFlags.None));
                            }
                            else
                            {
                                b.DrawText(new DL.TextRun(x, y + renderedLines, entry.Description, whiteFg, blackBg, DL.CellAttrFlags.None));
                            }
                        }
                        else
                        {
                            b.DrawText(new DL.TextRun(x, y + renderedLines, entry.Description, whiteFg, blackBg, DL.CellAttrFlags.None));
                        }
                        renderedLines++;
                    }
                    currentLine++;
                }
            }
        }
    }
}