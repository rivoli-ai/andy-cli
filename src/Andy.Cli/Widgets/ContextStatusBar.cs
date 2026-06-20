using System;
using Andy.Cli.Themes;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>Context-usage severity, derived from the share of the context window consumed.</summary>
    public enum UsageLevel
    {
        Normal,
        Warning,
        Critical
    }

    /// <summary>
    /// Persistent status bar at the bottom of the screen showing context usage metrics,
    /// live per-turn token counts, model information, and conversation state.
    /// </summary>
    public sealed class ContextStatusBar
    {
        private int _inputTokens;
        private int _outputTokens;
        private int _maxTokens;
        private int _turnCount;
        private string _modelName = string.Empty;
        private string _providerName = string.Empty;

        // Live per-turn stats (set every frame from the render loop while a turn is active).
        private TurnStats? _liveStats;

        // Foreground/accent colors. The row background always follows the active theme's
        // status-line background so the bar stays consistent across theme switches.
        private readonly DL.Rgb24 _fg = new DL.Rgb24(160, 160, 160);
        private readonly DL.Rgb24 _accent = new DL.Rgb24(100, 200, 120);
        private readonly DL.Rgb24 _dimFg = new DL.Rgb24(100, 100, 110);
        private readonly DL.Rgb24 _warningFg = new DL.Rgb24(200, 180, 80);
        private readonly DL.Rgb24 _criticalFg = new DL.Rgb24(200, 80, 80);

        /// <summary>
        /// Update token counts and context metrics for the current conversation.
        /// </summary>
        /// <param name="inputTokens">Cumulative input tokens for the session.</param>
        /// <param name="outputTokens">Cumulative output tokens for the session.</param>
        /// <param name="maxTokens">Model context window, or 0 if unknown.</param>
        /// <param name="turnCount">Conversation turns completed so far.</param>
        public void Update(int inputTokens, int outputTokens, int maxTokens, int turnCount)
        {
            _inputTokens = inputTokens;
            _outputTokens = outputTokens;
            _maxTokens = maxTokens;
            _turnCount = turnCount;
        }

        /// <summary>
        /// Update the live per-turn stats reference. Called every frame from the render loop
        /// so the status bar shows real-time input/output token counts while the turn is
        /// in flight. Pass <c>null</c> when idle.
        /// </summary>
        public void SetLiveStats(TurnStats? stats) => _liveStats = stats;

        /// <summary>
        /// Update model and provider information.
        /// </summary>
        public void SetModelInfo(string? modelName, string? providerName)
        {
            _modelName = modelName ?? string.Empty;
            _providerName = providerName ?? string.Empty;
        }

        private int TotalTokens => _inputTokens + _outputTokens;

        /// <summary>Usage severity for the current token total against the context window.</summary>
        internal UsageLevel Level
        {
            get
            {
                if (_maxTokens <= 0) return UsageLevel.Normal;
                double pct = (double)TotalTokens / _maxTokens * 100;
                if (pct >= 90) return UsageLevel.Critical;
                if (pct >= 70) return UsageLevel.Warning;
                return UsageLevel.Normal;
            }
        }

        /// <summary>Token usage string, e.g. "45.2K / 200.0K" (or just "45.2K" when no window known).</summary>
        internal string FormatUsage()
        {
            if (_maxTokens > 0)
                return $"{TokenFormat.Short(TotalTokens)} / {TokenFormat.Short(_maxTokens)}";
            return TokenFormat.Short(TotalTokens);
        }

        /// <summary>Percentage of the context window used, e.g. "(22.6%)", or empty when unknown.</summary>
        internal string FormatPercentage()
        {
            if (_maxTokens <= 0) return string.Empty;
            double pct = (double)TotalTokens / _maxTokens * 100;
            return $"({pct:F1}%)";
        }

        /// <summary>
        /// Format the live per-turn token counter for display.
        /// Returns e.g. "2.1K in / 480 out" while a turn is in progress,
        /// or empty string when idle.
        /// </summary>
        private string FormatLiveTurnTokens()
        {
            if (_liveStats == null || !_liveStats.IsActive) return string.Empty;

            var stats = _liveStats;
            var parts = new System.Collections.Generic.List<string>();
            if (stats.InputTokens > 0)
                parts.Add($"{TokenFormat.Short(stats.InputTokens)} in");
            if (stats.OutputTokens > 0)
                parts.Add($"{TokenFormat.Short(stats.OutputTokens)} out");
            if (parts.Count == 0) return string.Empty;
            return string.Join(" \u2192 ", parts);
        }

        /// <summary>
        /// Turns summary. When the context window is known we show an estimated number of turns
        /// remaining (based on average tokens/turn so far); otherwise we honestly show the count
        /// of turns elapsed.
        /// </summary>
        internal string FormatTurns()
        {
            if (_maxTokens > 0 && _turnCount > 0 && TotalTokens > 0)
            {
                double avgPerTurn = (double)TotalTokens / _turnCount;
                int remaining = avgPerTurn > 0
                    ? (int)Math.Max(0, (_maxTokens - TotalTokens) / avgPerTurn)
                    : 0;
                return $"~{remaining} turns left";
            }
            return _turnCount == 1 ? "1 turn" : $"{_turnCount} turns";
        }

        private DL.Rgb24 LevelColor() => Level switch
        {
            UsageLevel.Critical => _criticalFg,
            UsageLevel.Warning => _warningFg,
            _ => _accent
        };

        /// <summary>
        /// Render the context status bar at the bottom of the viewport.
        /// </summary>
        public void Render((int Width, int Height) viewport, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            var theme = Theme.Current;
            int y = viewport.Height - 1;
            int width = viewport.Width;

            if (width < 20) return; // Too narrow to render

            var bg = theme.StatusLineBackground;

            // Background
            b.PushClip(new DL.ClipPush(0, y, width, 1));
            b.DrawRect(new DL.Rect(0, y, width, 1, bg));

            string usageStr = FormatUsage();
            string percentageStr = FormatPercentage();
            string turnsStr = FormatTurns();
            string liveTokensStr = FormatLiveTurnTokens();

            // Left side: Model info
            int x = 1;
            if (!string.IsNullOrEmpty(_modelName))
            {
                string modelLabel = "Model: ";
                string modelNameShort = TruncateModelName(_modelName, 20);
                string providerPart = !string.IsNullOrEmpty(_providerName) ? $" ({_providerName})" : "";

                b.DrawText(new DL.TextRun(x, y, modelLabel, _dimFg, bg, DL.CellAttrFlags.None));
                x += modelLabel.Length;
                b.DrawText(new DL.TextRun(x, y, modelNameShort, _accent, bg, DL.CellAttrFlags.Bold));
                x += modelNameShort.Length;
                b.DrawText(new DL.TextRun(x, y, providerPart, _dimFg, bg, DL.CellAttrFlags.None));
                x += providerPart.Length;
            }

            // Build right-side content: live tokens (during turn) or cumulative usage + turns (idle)
            string rightSection;
            if (!string.IsNullOrEmpty(liveTokensStr))
            {
                // During a turn: show live per-turn in/out tokens
                rightSection = liveTokensStr;
            }
            else
            {
                // Idle: show cumulative usage + turns
                rightSection = string.IsNullOrEmpty(percentageStr)
                    ? $"{usageStr} {turnsStr}"
                    : $"{usageStr} {percentageStr} {turnsStr}";
            }

            int rightX = width - rightSection.Length - 2;
            if (rightX > x + 2)
            {
                // Draw separator
                b.DrawText(new DL.TextRun(rightX - 2, y, " | ", _dimFg, bg, DL.CellAttrFlags.None));

                if (!string.IsNullOrEmpty(liveTokensStr))
                {
                    // Live turn tokens: accent-colored, bold, with subtle background emphasis
                    b.DrawText(new DL.TextRun(rightX, y, liveTokensStr, _accent, bg, DL.CellAttrFlags.Bold));
                }
                else
                {
                    // Cumulative usage
                    b.DrawText(new DL.TextRun(rightX, y, usageStr, _fg, bg, DL.CellAttrFlags.None));
                    int cursor = rightX + usageStr.Length;

                    // Draw percentage with color based on usage level (only when known)
                    if (!string.IsNullOrEmpty(percentageStr))
                    {
                        b.DrawText(new DL.TextRun(cursor, y, $" {percentageStr}", LevelColor(), bg, DL.CellAttrFlags.Bold));
                        cursor += percentageStr.Length + 1;
                    }

                    // Draw turns
                    b.DrawText(new DL.TextRun(cursor, y, $" {turnsStr}", _dimFg, bg, DL.CellAttrFlags.None));
                }
            }

            b.Pop();
        }

        /// <summary>
        /// Get the width needed to render the status bar.
        /// </summary>
        public int GetWidth()
        {
            string rightSection = string.Join(" ", FormatUsage(), FormatPercentage(), FormatTurns()).Replace("  ", " ").Trim();
            string leftSection = string.IsNullOrEmpty(_modelName)
                ? string.Empty
                : $"Model: {TruncateModelName(_modelName, 20)}" +
                  (string.IsNullOrEmpty(_providerName) ? "" : $" ({_providerName})");

            return leftSection.Length + 4 + rightSection.Length; // +4 for separator and padding
        }

        /// <summary>
        /// Truncate model name if too long, dropping any provider prefix like "openai/".
        /// </summary>
        internal static string TruncateModelName(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var slashIndex = name.LastIndexOf('/');
            if (slashIndex > 0 && slashIndex < name.Length - 1)
            {
                name = name.Substring(slashIndex + 1);
            }
            if (name.Length <= maxLength) return name;
            return name.Substring(0, maxLength - 3) + "...";
        }
    }
}
