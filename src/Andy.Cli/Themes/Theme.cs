using System;
using System.Collections.Generic;
using System.Linq;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Themes
{
    /// <summary>
    /// Centralized theme configuration for consistent colors throughout the application.
    /// </summary>
    public sealed class Theme
    {
        /// <summary>
        /// Human-readable, lookup-friendly name for this theme (e.g. "dark", "light").
        /// </summary>
        public string Name { get; set; } = "dark";

        // Background colors.
        // null = transparent: the terminal's own background (including transparency)
        // shows through. Main surface, header, prompt and dialog backgrounds are
        // transparent; the remaining chrome keeps a subtle shade for legibility.
        public DL.Rgb24? Background { get; set; } = null;
        public DL.Rgb24? HeaderBackground { get; set; } = null;
        public DL.Rgb24? DialogBackground { get; set; } = null;
        public DL.Rgb24 CodeBlockBackground { get; set; } = new DL.Rgb24(20, 20, 30);
        public DL.Rgb24? PromptBackground { get; set; } = null;
        public DL.Rgb24 ToastBackground { get; set; } = new DL.Rgb24(60, 60, 20);
        public DL.Rgb24 StatusLineBackground { get; set; } = new DL.Rgb24(10, 10, 10);
        public DL.Rgb24 KeyHintsBackground { get; set; } = new DL.Rgb24(15, 15, 15);

        // Foreground/Text colors
        public DL.Rgb24 Text { get; set; } = new DL.Rgb24(220, 220, 220);
        public DL.Rgb24 TextDim { get; set; } = new DL.Rgb24(150, 150, 150);
        public DL.Rgb24 TextBright { get; set; } = new DL.Rgb24(255, 255, 255);

        // Accent colors
        public DL.Rgb24 Primary { get; set; } = new DL.Rgb24(150, 200, 255);
        public DL.Rgb24 Secondary { get; set; } = new DL.Rgb24(200, 200, 80);
        public DL.Rgb24 Accent { get; set; } = new DL.Rgb24(180, 200, 255);

        // Status colors
        public DL.Rgb24 Success { get; set; } = new DL.Rgb24(0, 255, 0);
        public DL.Rgb24 Warning { get; set; } = new DL.Rgb24(255, 200, 0);
        public DL.Rgb24 Error { get; set; } = new DL.Rgb24(255, 80, 80);
        public DL.Rgb24 Info { get; set; } = new DL.Rgb24(100, 200, 255);

        // Special colors
        public DL.Rgb24 Heading { get; set; } = new DL.Rgb24(200, 200, 80);
        public DL.Rgb24 Code { get; set; } = new DL.Rgb24(180, 180, 180);
        public DL.Rgb24 Ghost { get; set; } = new DL.Rgb24(100, 100, 100);
        public DL.Rgb24 Border { get; set; } = new DL.Rgb24(80, 80, 80);
        public DL.Rgb24 Separator { get; set; } = new DL.Rgb24(50, 55, 70);
        public DL.Rgb24 KeyHighlight { get; set; } = new DL.Rgb24(200, 200, 80);

        // Header colors
        public DL.Rgb24 HeaderTitle { get; set; } = new DL.Rgb24(250, 250, 100);
        public DL.Rgb24 HeaderPath { get; set; } = new DL.Rgb24(150, 180, 200);
        public DL.Rgb24 HeaderGitInfo { get; set; } = new DL.Rgb24(200, 200, 50);
        public DL.Rgb24 HeaderDelimiter { get; set; } = new DL.Rgb24(100, 100, 120);

        // Response separator colors
        public DL.Rgb24 SeparatorBase { get; set; } = new DL.Rgb24(120, 140, 160);
        public DL.Rgb24 SeparatorAccent { get; set; } = new DL.Rgb24(180, 200, 255);
        public DL.Rgb24 SeparatorToken { get; set; } = new DL.Rgb24(150, 170, 140);

        // Metadata colors (time elapsed, context info, etc.)
        public DL.Rgb24 Metadata { get; set; } = new DL.Rgb24(100, 150, 200);

        // User message colors
        public DL.Rgb24 UserLabel { get; set; } = new DL.Rgb24(100, 200, 100);
        public DL.Rgb24 UserText { get; set; } = new DL.Rgb24(200, 255, 200);

        // Tool execution colors
        public DL.Rgb24 ToolName { get; set; } = new DL.Rgb24(255, 200, 100);
        public DL.Rgb24 ToolRunning { get; set; } = new DL.Rgb24(100, 150, 255);
        public DL.Rgb24 ToolResult { get; set; } = new DL.Rgb24(180, 180, 180);

        /// <summary>
        /// Default dark theme.
        /// </summary>
        public static Theme Dark { get; } = new Theme { Name = "dark" };

        /// <summary>
        /// A light theme suited to terminals with a bright background.
        /// </summary>
        public static Theme Light { get; } = new Theme
        {
            Name = "light",
            // Keep main surfaces transparent so the terminal background shows through,
            // but use darker text/accents that remain legible on a light background.
            Background = null,
            HeaderBackground = null,
            DialogBackground = null,
            CodeBlockBackground = new DL.Rgb24(235, 235, 240),
            PromptBackground = null,
            ToastBackground = new DL.Rgb24(240, 235, 180),
            StatusLineBackground = new DL.Rgb24(225, 225, 225),
            KeyHintsBackground = new DL.Rgb24(230, 230, 230),

            Text = new DL.Rgb24(40, 40, 40),
            TextDim = new DL.Rgb24(110, 110, 110),
            TextBright = new DL.Rgb24(0, 0, 0),

            Primary = new DL.Rgb24(30, 90, 170),
            Secondary = new DL.Rgb24(140, 110, 0),
            Accent = new DL.Rgb24(50, 100, 180),

            Success = new DL.Rgb24(0, 140, 0),
            Warning = new DL.Rgb24(170, 120, 0),
            Error = new DL.Rgb24(190, 30, 30),
            Info = new DL.Rgb24(20, 110, 180),

            Heading = new DL.Rgb24(140, 110, 0),
            Code = new DL.Rgb24(70, 70, 70),
            Ghost = new DL.Rgb24(160, 160, 160),
            Border = new DL.Rgb24(170, 170, 170),
            Separator = new DL.Rgb24(180, 185, 200),
            KeyHighlight = new DL.Rgb24(140, 110, 0),

            HeaderTitle = new DL.Rgb24(120, 100, 0),
            HeaderPath = new DL.Rgb24(60, 90, 120),
            HeaderGitInfo = new DL.Rgb24(140, 110, 0),
            HeaderDelimiter = new DL.Rgb24(150, 150, 170),

            SeparatorBase = new DL.Rgb24(120, 130, 150),
            SeparatorAccent = new DL.Rgb24(50, 100, 180),
            SeparatorToken = new DL.Rgb24(110, 130, 100),

            Metadata = new DL.Rgb24(60, 100, 150),

            UserLabel = new DL.Rgb24(0, 130, 0),
            UserText = new DL.Rgb24(30, 90, 30),

            ToolName = new DL.Rgb24(170, 110, 0),
            ToolRunning = new DL.Rgb24(40, 90, 180),
            ToolResult = new DL.Rgb24(90, 90, 90),
        };

        /// <summary>
        /// Registry of all predefined themes, keyed by their lower-case name.
        /// </summary>
        private static readonly Dictionary<string, Theme> Registry =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [Dark.Name] = Dark,
                [Light.Name] = Light,
            };

        /// <summary>
        /// The names of all available predefined themes, in a stable order.
        /// </summary>
        public static IReadOnlyList<string> AvailableThemes { get; } =
            new[] { Dark.Name, Light.Name };

        /// <summary>
        /// Look up a predefined theme by name (case-insensitive).
        /// Returns null when no theme matches.
        /// </summary>
        public static Theme? GetByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            return Registry.TryGetValue(name.Trim(), out var theme) ? theme : null;
        }

        /// <summary>
        /// Get the current active theme.
        /// </summary>
        public static Theme Current { get; set; } = Dark;
    }
}
