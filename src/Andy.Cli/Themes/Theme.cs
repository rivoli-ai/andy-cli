using System;
using System.Collections.Generic;
using System.Linq;
using DL = Andy.Tui.DisplayList;
using TS = Andy.Tui.Style;

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

        // Whether this theme offers the optional transparent-background mode (the
        // main surfaces become null so the terminal background shows through). Dark
        // themes opt in; light-background themes opt out because their dark text is
        // unreadable when a dark terminal shows through.
        public bool OffersTransparentBackground { get; set; } = true;

        // Foreground/Text colors
        public DL.Rgb24 Text { get; set; } = new DL.Rgb24(220, 220, 220);
        public DL.Rgb24 TextDim { get; set; } = new DL.Rgb24(150, 150, 150);
        public DL.Rgb24 TextBright { get; set; } = new DL.Rgb24(255, 255, 255);

        // Color used for the text the user types into the prompt input.
        //
        // null = use the terminal's own DEFAULT foreground color (emitted as the
        // ANSI SGR 39 sequence). This is the only choice that is guaranteed to be
        // readable regardless of the user's actual terminal background, which the
        // application cannot reliably detect. Because the prompt background is also
        // transparent (PromptBackground = null -> SGR 49, the terminal default
        // background), forcing an explicit RGB foreground risks unreadable text
        // (e.g. near-black text on a dark terminal under the light theme). Leaving
        // both unset lets the terminal's own fg/bg pair apply, which is inherently
        // legible. Both themes therefore default this to null.
        public DL.Rgb24? PromptText { get; set; } = null;

        // Accent colors
        public DL.Rgb24 Primary { get; set; } = new DL.Rgb24(150, 200, 255);
        public DL.Rgb24 Secondary { get; set; } = new DL.Rgb24(200, 200, 80);
        public DL.Rgb24 Accent { get; set; } = new DL.Rgb24(180, 200, 255);

        // Status colors
        public DL.Rgb24 Success { get; set; } = new DL.Rgb24(0, 255, 0);
        public DL.Rgb24 Warning { get; set; } = new DL.Rgb24(255, 200, 0);
        public DL.Rgb24 Error { get; set; } = new DL.Rgb24(255, 80, 80);
        public DL.Rgb24 Info { get; set; } = new DL.Rgb24(100, 200, 255);

        // Special colors. Heading is the distinct dark-orange used for markdown headers in the feed.
        public DL.Rgb24 Heading { get; set; } = new DL.Rgb24(215, 125, 40);
        public DL.Rgb24 Code { get; set; } = new DL.Rgb24(180, 180, 180);
        public DL.Rgb24 Ghost { get; set; } = new DL.Rgb24(100, 100, 100);

        // Syntax highlighting colors for code blocks and inline code. Distinct theme
        // colors (never underlines) so keywords, types/class names, strings, comments
        // and numbers are easy to tell apart. Defaults suit a dark background.
        public DL.Rgb24 SyntaxKeyword { get; set; } = new DL.Rgb24(197, 134, 192); // purple
        public DL.Rgb24 SyntaxType { get; set; } = new DL.Rgb24(78, 201, 176);  // teal
        public DL.Rgb24 SyntaxString { get; set; } = new DL.Rgb24(206, 145, 120); // orange
        public DL.Rgb24 SyntaxComment { get; set; } = new DL.Rgb24(106, 153, 85);  // green
        public DL.Rgb24 SyntaxNumber { get; set; } = new DL.Rgb24(181, 206, 168); // light green
        public DL.Rgb24 SyntaxIdentifier { get; set; } = new DL.Rgb24(220, 220, 220); // near-default
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
        /// True when all four main surfaces are transparent (terminal shows through).
        /// </summary>
        public bool HasTransparentBackground =>
            Background is null && HeaderBackground is null
            && DialogBackground is null && PromptBackground is null;

        /// <summary>Create a shallow copy of this theme (all colors are value types).</summary>
        public Theme Clone() => (Theme)MemberwiseClone();

        /// <summary>
        /// Return a copy with the main surfaces made transparent so the terminal
        /// background shows through. Chrome surfaces (status line, key hints, toast,
        /// code block) keep a shade for legibility.
        /// </summary>
        public Theme WithTransparentBackground()
        {
            var t = Clone();
            t.Background = null;
            t.HeaderBackground = null;
            t.DialogBackground = null;
            t.PromptBackground = null;
            return t;
        }

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
            // PromptText intentionally left null (inherits the base default): the
            // prompt uses the terminal's own default foreground so it stays
            // readable even when a light theme is used in a dark terminal.
            PromptText = null,

            Primary = new DL.Rgb24(30, 90, 170),
            Secondary = new DL.Rgb24(140, 110, 0),
            Accent = new DL.Rgb24(50, 100, 180),

            Success = new DL.Rgb24(0, 140, 0),
            Warning = new DL.Rgb24(170, 120, 0),
            Error = new DL.Rgb24(190, 30, 30),
            Info = new DL.Rgb24(20, 110, 180),

            Heading = new DL.Rgb24(180, 85, 10),
            Code = new DL.Rgb24(70, 70, 70),
            Ghost = new DL.Rgb24(160, 160, 160),
            SyntaxKeyword = new DL.Rgb24(0, 0, 255),
            SyntaxType = new DL.Rgb24(38, 127, 153),
            SyntaxString = new DL.Rgb24(163, 21, 21),
            SyntaxComment = new DL.Rgb24(0, 128, 0),
            SyntaxNumber = new DL.Rgb24(9, 134, 88),
            SyntaxIdentifier = new DL.Rgb24(40, 40, 40),
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

        // Map an Andy.Tui token-based theme onto this app's richer color model. The
        // palette data lives in the Andy.Tui library (Andy.Tui.Style.PopularThemes);
        // this only maps the library's semantic tokens onto the application's roles,
        // so there is a single source of truth for the palettes.
        private static Theme FromLibrary(TS.Theme lib)
        {
            DL.Rgb24 C(TS.ThemeToken token) => lib.GetRgb(token, default);
            return new Theme
            {
                Name = lib.Name,
                // Dark-background themes offer the transparent-background option;
                // light ones do not (dark text would be unreadable on a dark terminal).
                OffersTransparentBackground = IsDark(C(TS.ThemeToken.Background)),

                // Header, prompt and footer share the body background so the surface
                // reads as one cohesive color; dialogs/code/toasts intentionally stand
                // out with a raised surface.
                Background = C(TS.ThemeToken.Background),
                HeaderBackground = C(TS.ThemeToken.Background),
                DialogBackground = C(TS.ThemeToken.Surface),
                CodeBlockBackground = C(TS.ThemeToken.Surface),
                PromptBackground = C(TS.ThemeToken.Background),
                ToastBackground = C(TS.ThemeToken.SurfaceSelected),
                StatusLineBackground = C(TS.ThemeToken.Background),
                KeyHintsBackground = C(TS.ThemeToken.Background),

                Text = C(TS.ThemeToken.Foreground),
                TextDim = C(TS.ThemeToken.ForegroundMuted),
                TextBright = C(TS.ThemeToken.Foreground),
                PromptText = C(TS.ThemeToken.Foreground),

                Primary = C(TS.ThemeToken.Accent),
                Secondary = C(TS.ThemeToken.Warning),
                Accent = C(TS.ThemeToken.Accent),

                Success = C(TS.ThemeToken.Success),
                Warning = C(TS.ThemeToken.Warning),
                Error = C(TS.ThemeToken.Error),
                Info = C(TS.ThemeToken.Info),

                // Distinct dark orange for markdown headers (kept off the accent, which is also used
                // for links/emphasis, so headers read as headers across imported palettes).
                Heading = new DL.Rgb24(215, 125, 40),
                Code = C(TS.ThemeToken.SyntaxPreproc),
                Ghost = C(TS.ThemeToken.ForegroundMuted),
                SyntaxKeyword = C(TS.ThemeToken.SyntaxKeyword),
                SyntaxType = C(TS.ThemeToken.SyntaxPreproc),
                SyntaxString = C(TS.ThemeToken.SyntaxString),
                SyntaxComment = C(TS.ThemeToken.SyntaxComment),
                SyntaxNumber = C(TS.ThemeToken.SyntaxNumber),
                SyntaxIdentifier = C(TS.ThemeToken.Foreground),
                Border = C(TS.ThemeToken.Border),
                Separator = C(TS.ThemeToken.Border),
                KeyHighlight = C(TS.ThemeToken.Warning),

                HeaderTitle = C(TS.ThemeToken.Accent),
                HeaderPath = C(TS.ThemeToken.Info),
                HeaderGitInfo = C(TS.ThemeToken.Success),
                HeaderDelimiter = C(TS.ThemeToken.ForegroundMuted),

                SeparatorBase = C(TS.ThemeToken.ForegroundMuted),
                SeparatorAccent = C(TS.ThemeToken.Accent),
                SeparatorToken = C(TS.ThemeToken.Success),

                Metadata = C(TS.ThemeToken.Info),

                UserLabel = C(TS.ThemeToken.Success),
                UserText = C(TS.ThemeToken.Foreground),

                ToolName = C(TS.ThemeToken.Accent),
                ToolRunning = C(TS.ThemeToken.Info),
                ToolResult = C(TS.ThemeToken.Success),
            };
        }

        // Perceived luminance below the midpoint => treat as a dark background.
        private static bool IsDark(DL.Rgb24 c)
            => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;

        /// <summary>
        /// Popular vim/terminal/TUI color schemes, sourced from the Andy.Tui theme
        /// library and mapped onto this application's palette.
        /// </summary>
        private static readonly Theme[] Popular =
            TS.PopularThemes.All.Select(FromLibrary).ToArray();

        /// <summary>
        /// Registry of all predefined themes, keyed by their lower-case name.
        /// </summary>
        private static readonly Dictionary<string, Theme> Registry = BuildRegistry();

        private static Dictionary<string, Theme> BuildRegistry()
        {
            var map = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase)
            {
                [Dark.Name] = Dark,
                [Light.Name] = Light,
            };
            foreach (var theme in Popular)
            {
                map[theme.Name] = theme;
            }
            return map;
        }

        /// <summary>
        /// The names of all available predefined themes, in a stable order.
        /// </summary>
        public static IReadOnlyList<string> AvailableThemes { get; } =
            new[] { Dark.Name, Light.Name }.Concat(Popular.Select(t => t.Name)).ToArray();

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
        /// Resolve a theme by name and apply the transparent-background preference.
        /// Transparency is only applied when the theme offers it. Returns null when
        /// the name does not match a predefined theme.
        /// </summary>
        public static Theme? Resolve(string? name, bool transparentBackground)
        {
            var theme = GetByName(name);
            if (theme == null)
                return null;
            return transparentBackground && theme.OffersTransparentBackground
                ? theme.WithTransparentBackground()
                : theme;
        }

        /// <summary>
        /// Get the current active theme.
        /// </summary>
        public static Theme Current { get; set; } = Dark;
    }
}
