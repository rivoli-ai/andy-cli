using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Themes
{
    /// <summary>
    /// Centralized theme configuration for consistent colors throughout the application.
    /// </summary>
    public sealed class Theme
    {
        // Background colors
        public DL.Rgb24 Background { get; set; } = new DL.Rgb24(0, 0, 0);
        public DL.Rgb24 HeaderBackground { get; set; } = new DL.Rgb24(30, 35, 50);
        public DL.Rgb24 DialogBackground { get; set; } = new DL.Rgb24(30, 30, 40);
        public DL.Rgb24 CodeBlockBackground { get; set; } = new DL.Rgb24(20, 20, 30);
        public DL.Rgb24 PromptBackground { get; set; } = new DL.Rgb24(0, 0, 0);
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
        public static Theme Dark { get; } = new Theme();

        /// <summary>
        /// Get the current active theme (can be extended for theme switching).
        /// </summary>
        public static Theme Current { get; set; } = Dark;
    }
}
