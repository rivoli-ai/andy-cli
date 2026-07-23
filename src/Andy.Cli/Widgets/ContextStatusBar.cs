using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
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

    /// <summary>Kinds of segments composed into the single status line.</summary>
    internal enum StatusSegmentKind
    {
        Status,
        Model,
        Usage,
        Live,
        Cost,
        Turns
    }

    /// <summary>
    /// One segment of the collapsed status line. Segments are rendered in display order,
    /// separated by " | ". When the line would overflow the available width, segments are
    /// dropped in descending <see cref="DropPriority"/> order (highest dropped first).
    /// </summary>
    internal readonly record struct StatusSegment(StatusSegmentKind Kind, string Text, int DropPriority);

    /// <summary>
    /// Single persistent status line at the bottom of the screen. Collapses what used to be
    /// three rows (status message, key hints, context bar) into one line showing the current
    /// status, model information, token usage, session cost, and turn count, separated by
    /// a simple ASCII " | " separator. Least important segments are dropped first when the
    /// terminal is too narrow.
    /// </summary>
    public sealed class ContextStatusBar
    {
        internal const string Separator = " | ";

        private int _inputTokens;
        private int _outputTokens;
        private int _maxTokens;
        private int _turnCount;
        private string _modelName = string.Empty;
        private string _providerName = string.Empty;

        // Transient status text (e.g. "Thinking"), replaces the old StatusMessage row.
        private string _statusText = string.Empty;
        private bool _statusAnimated;
        private DateTime _statusSince = DateTime.Now;

        // Live per-turn stats (set every frame from the render loop while a turn is active).
        private TurnStats? _liveStats;

        // Foreground/accent colors. The row background always follows the active theme's
        // status-line background so the bar stays consistent across theme switches.
        private readonly DL.Rgb24 _fg = new DL.Rgb24(160, 160, 160);
        private readonly DL.Rgb24 _accent = new DL.Rgb24(100, 200, 120);
        private readonly DL.Rgb24 _dimFg = new DL.Rgb24(100, 100, 110);
        private readonly DL.Rgb24 _warningFg = new DL.Rgb24(200, 180, 80);
        private readonly DL.Rgb24 _criticalFg = new DL.Rgb24(200, 80, 80);
        private readonly DL.Rgb24 _statusFg = new DL.Rgb24(150, 200, 255);

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

        /// <summary>
        /// Set the transient status text shown as the first segment of the line
        /// (e.g. "Thinking" while a request is in flight). Pass an empty string to clear.
        /// When <paramref name="animated"/> is true, trailing dots cycle over time.
        /// </summary>
        public void SetStatusText(string? text, bool animated = false)
        {
            var newText = text ?? string.Empty;
            if (newText != _statusText)
            {
                _statusSince = DateTime.Now;
            }
            _statusText = newText;
            _statusAnimated = animated;
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
        /// Cumulative session cost in USD formatted for display (e.g. "$0.0123"),
        /// or empty when the model's pricing is unknown or no tokens were used yet.
        /// </summary>
        internal string FormatCost()
        {
            if (TotalTokens <= 0) return string.Empty;
            var cost = ModelPricing.ComputeCostUsd(_modelName, _providerName, _inputTokens, _outputTokens);
            return cost == null ? string.Empty : ModelPricing.FormatUsd(cost.Value);
        }

        /// <summary>
        /// Format the live per-turn token counter for display.
        /// Returns e.g. "2.1K in -> 480 out ($0.0123)" while a turn is in progress,
        /// or empty string when idle. The per-request cost is appended when pricing is known.
        /// </summary>
        internal string FormatLiveTurnTokens()
        {
            if (_liveStats == null || !_liveStats.IsActive) return string.Empty;

            var stats = _liveStats;
            var parts = new List<string>();
            if (stats.InputTokens > 0)
                parts.Add($"{TokenFormat.Short(stats.InputTokens)} in");
            if (stats.OutputTokens > 0)
                parts.Add($"{TokenFormat.Short(stats.OutputTokens)} out");
            if (parts.Count == 0) return string.Empty;

            string live = string.Join(" -> ", parts);
            var cost = ModelPricing.ComputeCostUsd(_modelName, _providerName, stats.InputTokens, stats.OutputTokens);
            if (cost != null)
                live += $" ({ModelPricing.FormatUsd(cost.Value)})";
            return live;
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

        private string FormatStatus()
        {
            if (string.IsNullOrEmpty(_statusText)) return string.Empty;
            if (!_statusAnimated) return _statusText;
            double elapsed = (DateTime.Now - _statusSince).TotalSeconds;
            int dotCount = (int)(elapsed * 2) % 4; // cycle 0-3 dots every 2 seconds
            return _statusText + new string('.', dotCount);
        }

        private DL.Rgb24 LevelColor() => Level switch
        {
            UsageLevel.Critical => _criticalFg,
            UsageLevel.Warning => _warningFg,
            _ => _accent
        };

        /// <summary>
        /// Build the segments of the status line in display order. Drop priorities:
        /// model (1) is kept the longest, then usage (2), cost (3), status (4), turns (5).
        /// </summary>
        internal List<StatusSegment> BuildSegments()
        {
            var segments = new List<StatusSegment>();

            string statusStr = FormatStatus();
            if (!string.IsNullOrEmpty(statusStr))
                segments.Add(new StatusSegment(StatusSegmentKind.Status, statusStr, 4));

            if (!string.IsNullOrEmpty(_modelName))
                segments.Add(new StatusSegment(StatusSegmentKind.Model, ModelSegmentText(), 1));

            string liveStr = FormatLiveTurnTokens();
            if (!string.IsNullOrEmpty(liveStr))
            {
                segments.Add(new StatusSegment(StatusSegmentKind.Live, liveStr, 2));
            }
            else
            {
                segments.Add(new StatusSegment(StatusSegmentKind.Usage, UsageSegmentText(), 2));
            }

            string costStr = FormatCost();
            if (!string.IsNullOrEmpty(costStr))
                segments.Add(new StatusSegment(StatusSegmentKind.Cost, costStr, 3));

            segments.Add(new StatusSegment(StatusSegmentKind.Turns, FormatTurns(), 5));

            return segments;
        }

        private string ModelSegmentText()
        {
            string modelNameShort = TruncateModelName(_modelName, 20);
            string providerPart = !string.IsNullOrEmpty(_providerName) ? $" ({_providerName})" : "";
            return $"Model: {modelNameShort}{providerPart}";
        }

        private string UsageSegmentText()
        {
            string usageStr = FormatUsage();
            string percentageStr = FormatPercentage();
            return string.IsNullOrEmpty(percentageStr) ? usageStr : $"{usageStr} {percentageStr}";
        }

        /// <summary>
        /// Fit the segments into <paramref name="maxWidth"/> columns. Segments are removed
        /// in descending drop-priority order (least important first) until the joined line
        /// fits. If the single remaining segment is still too wide it is truncated with
        /// an ASCII ellipsis.
        /// </summary>
        internal static List<StatusSegment> FitSegments(List<StatusSegment> segments, int maxWidth)
        {
            var fitted = new List<StatusSegment>(segments);

            static int JoinedWidth(List<StatusSegment> segs) =>
                segs.Sum(s => s.Text.Length) + Math.Max(0, segs.Count - 1) * Separator.Length;

            while (fitted.Count > 1 && JoinedWidth(fitted) > maxWidth)
            {
                int dropIndex = 0;
                for (int i = 1; i < fitted.Count; i++)
                {
                    if (fitted[i].DropPriority >= fitted[dropIndex].DropPriority)
                        dropIndex = i;
                }
                fitted.RemoveAt(dropIndex);
            }

            if (fitted.Count == 1 && fitted[0].Text.Length > maxWidth)
            {
                if (maxWidth < 4)
                {
                    fitted.Clear();
                }
                else
                {
                    var s = fitted[0];
                    fitted[0] = s with { Text = s.Text.Substring(0, maxWidth - 3) + "..." };
                }
            }

            return fitted;
        }

        /// <summary>
        /// Render the collapsed status line on the bottom row of the viewport.
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

            int x = 1;
            int avail = Math.Max(0, width - 2);
            var segments = FitSegments(BuildSegments(), avail);

            for (int i = 0; i < segments.Count; i++)
            {
                if (i > 0)
                {
                    b.DrawText(new DL.TextRun(x, y, Separator, _dimFg, bg, DL.CellAttrFlags.None));
                    x += Separator.Length;
                }
                x = DrawSegment(b, x, y, segments[i], bg);
            }

            b.Pop();
        }

        private int DrawSegment(DL.DisplayListBuilder b, int x, int y, StatusSegment segment, DL.Rgb24? bg)
        {
            switch (segment.Kind)
            {
                case StatusSegmentKind.Model when segment.Text == ModelSegmentText():
                    {
                        // Untruncated model segment: label dim, name accented, provider dim.
                        string modelLabel = "Model: ";
                        string modelNameShort = TruncateModelName(_modelName, 20);
                        string providerPart = !string.IsNullOrEmpty(_providerName) ? $" ({_providerName})" : "";

                        b.DrawText(new DL.TextRun(x, y, modelLabel, _dimFg, bg, DL.CellAttrFlags.None));
                        x += modelLabel.Length;
                        b.DrawText(new DL.TextRun(x, y, modelNameShort, _accent, bg, DL.CellAttrFlags.Bold));
                        x += modelNameShort.Length;
                        if (providerPart.Length > 0)
                        {
                            b.DrawText(new DL.TextRun(x, y, providerPart, _dimFg, bg, DL.CellAttrFlags.None));
                            x += providerPart.Length;
                        }
                        return x;
                    }
                case StatusSegmentKind.Usage when segment.Text == UsageSegmentText():
                    {
                        // Untruncated usage segment: counts plain, percentage colored by level.
                        string usageStr = FormatUsage();
                        string percentageStr = FormatPercentage();

                        b.DrawText(new DL.TextRun(x, y, usageStr, _fg, bg, DL.CellAttrFlags.None));
                        x += usageStr.Length;
                        if (!string.IsNullOrEmpty(percentageStr))
                        {
                            b.DrawText(new DL.TextRun(x, y, $" {percentageStr}", LevelColor(), bg, DL.CellAttrFlags.Bold));
                            x += percentageStr.Length + 1;
                        }
                        return x;
                    }
                case StatusSegmentKind.Live:
                    b.DrawText(new DL.TextRun(x, y, segment.Text, _accent, bg, DL.CellAttrFlags.Bold));
                    return x + segment.Text.Length;
                case StatusSegmentKind.Status:
                    b.DrawText(new DL.TextRun(x, y, segment.Text, _statusFg, bg, DL.CellAttrFlags.None));
                    return x + segment.Text.Length;
                case StatusSegmentKind.Cost:
                    b.DrawText(new DL.TextRun(x, y, segment.Text, _fg, bg, DL.CellAttrFlags.Bold));
                    return x + segment.Text.Length;
                case StatusSegmentKind.Turns:
                    b.DrawText(new DL.TextRun(x, y, segment.Text, _dimFg, bg, DL.CellAttrFlags.None));
                    return x + segment.Text.Length;
                default:
                    b.DrawText(new DL.TextRun(x, y, segment.Text, _fg, bg, DL.CellAttrFlags.None));
                    return x + segment.Text.Length;
            }
        }

        /// <summary>
        /// Get the width needed to render the full (undropped) status line.
        /// </summary>
        public int GetWidth()
        {
            var segments = BuildSegments();
            return segments.Sum(s => s.Text.Length) + Math.Max(0, segments.Count - 1) * Separator.Length;
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
