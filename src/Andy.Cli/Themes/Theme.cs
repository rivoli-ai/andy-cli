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
        // Kept distinct from the accent (Primary) color so it stays a
        // high-contrast, easily readable body-text color on the prompt
        // background across themes. Defaults to a near-white tone suited to
        // the dark theme; light theme overrides it with a near-black tone.
        public DL.Rgb24 PromptText { get; set; } = new DL.Rgb24(235, 235, 235);

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
            PromptText = new DL.Rgb24(25, 25, 25),

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

        private static DL.Rgb24 Hex(string hex)
        {
            int o = hex.Length > 0 && hex[0] == '#' ? 1 : 0;
            byte r = Convert.ToByte(hex.Substring(o, 2), 16);
            byte g = Convert.ToByte(hex.Substring(o + 2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(o + 4, 2), 16);
            return new DL.Rgb24(r, g, b);
        }

        /// <summary>
        /// Build a theme from a compact core palette (popular vim/terminal schemes).
        /// The core colors are mapped to the application's semantic roles consistently
        /// across all ported themes.
        /// </summary>
        private static Theme FromPalette(string name,
            string bg, string bg1, string bg2, string sel, string fg, string dim, string accent,
            string red, string green, string yellow, string blue, string magenta, string cyan,
            bool offersTransparency = true)
            => new Theme
            {
                Name = name,
                OffersTransparentBackground = offersTransparency,

                Background = Hex(bg),
                HeaderBackground = Hex(bg1),
                DialogBackground = Hex(bg1),
                CodeBlockBackground = Hex(bg1),
                PromptBackground = Hex(bg),
                ToastBackground = Hex(sel),
                StatusLineBackground = Hex(bg1),
                KeyHintsBackground = Hex(bg2),

                Text = Hex(fg),
                TextDim = Hex(dim),
                TextBright = Hex(fg),
                PromptText = Hex(fg),

                Primary = Hex(accent),
                Secondary = Hex(yellow),
                Accent = Hex(accent),

                Success = Hex(green),
                Warning = Hex(yellow),
                Error = Hex(red),
                Info = Hex(blue),

                Heading = Hex(accent),
                Code = Hex(cyan),
                Ghost = Hex(dim),
                Border = Hex(bg2),
                Separator = Hex(bg2),
                KeyHighlight = Hex(yellow),

                HeaderTitle = Hex(accent),
                HeaderPath = Hex(blue),
                HeaderGitInfo = Hex(green),
                HeaderDelimiter = Hex(dim),

                SeparatorBase = Hex(dim),
                SeparatorAccent = Hex(accent),
                SeparatorToken = Hex(green),

                Metadata = Hex(blue),

                UserLabel = Hex(green),
                UserText = Hex(fg),

                ToolName = Hex(accent),
                ToolRunning = Hex(blue),
                ToolResult = Hex(green),
            };

        /// <summary>
        /// Popular vim/terminal/TUI color schemes, ported to the application palette.
        /// Light-background themes opt out of the transparent-background option.
        /// </summary>
        private static readonly Theme[] Popular =
        {
            //          name                bg        bg1       bg2       sel       fg        dim       accent    red       green     yellow    blue      magenta   cyan
            FromPalette("gruvbox-dark",      "282828", "3c3836", "504945", "665c54", "ebdbb2", "928374", "fe8019", "fb4934", "b8bb26", "fabd2f", "83a598", "d3869b", "8ec07c"),
            FromPalette("gruvbox-light",     "fbf1c7", "ebdbb2", "d5c4a1", "bdae93", "3c3836", "7c6f64", "af3a03", "9d0006", "79740e", "b57614", "076678", "8f3f71", "427b58", offersTransparency: false),
            FromPalette("solarized-dark",    "002b36", "073642", "586e75", "094f5e", "839496", "586e75", "268bd2", "dc322f", "859900", "b58900", "268bd2", "d33682", "2aa198"),
            FromPalette("solarized-light",   "fdf6e3", "eee8d5", "93a1a1", "d9d2bf", "657b83", "93a1a1", "268bd2", "dc322f", "859900", "b58900", "268bd2", "d33682", "2aa198", offersTransparency: false),
            FromPalette("dracula",           "282a36", "343746", "44475a", "44475a", "f8f8f2", "6272a4", "bd93f9", "ff5555", "50fa7b", "f1fa8c", "8be9fd", "ff79c6", "8be9fd"),
            FromPalette("nord",              "2e3440", "3b4252", "434c5e", "4c566a", "d8dee9", "616e88", "88c0d0", "bf616a", "a3be8c", "ebcb8b", "81a1c1", "b48ead", "8fbcbb"),
            FromPalette("monokai",           "272822", "3e3d32", "49483e", "49483e", "f8f8f2", "75715e", "f92672", "f92672", "a6e22e", "e6db74", "66d9ef", "ae81ff", "66d9ef"),
            FromPalette("one-dark",          "282c34", "2c313c", "3b4048", "3e4451", "abb2bf", "5c6370", "61afef", "e06c75", "98c379", "e5c07b", "61afef", "c678dd", "56b6c2"),
            FromPalette("one-light",         "fafafa", "f0f0f0", "e5e5e6", "dbdbdc", "383a42", "a0a1a7", "4078f2", "e45649", "50a14f", "c18401", "4078f2", "a626a4", "0184bc", offersTransparency: false),
            FromPalette("tokyo-night",       "1a1b26", "1f2335", "292e42", "33467c", "c0caf5", "565f89", "7aa2f7", "f7768e", "9ece6a", "e0af68", "7aa2f7", "bb9af7", "7dcfff"),
            FromPalette("tokyo-night-storm", "24283b", "1f2335", "292e42", "2e3c64", "c0caf5", "565f89", "7aa2f7", "f7768e", "9ece6a", "e0af68", "7aa2f7", "bb9af7", "7dcfff"),
            FromPalette("tokyo-night-day",   "e1e2e7", "d5d6db", "c4c8da", "b7c1e3", "343b58", "848cb5", "2e7de9", "f52a65", "587539", "8c6c3e", "2e7de9", "9854f1", "007197", offersTransparency: false),
            FromPalette("catppuccin-mocha",  "1e1e2e", "313244", "45475a", "585b70", "cdd6f4", "6c7086", "cba6f7", "f38ba8", "a6e3a1", "f9e2af", "89b4fa", "cba6f7", "94e2d5"),
            FromPalette("catppuccin-macchiato", "24273a", "363a4f", "494d64", "5b6078", "cad3f5", "6e738d", "c6a0f6", "ed8796", "a6da95", "eed49f", "8aadf4", "c6a0f6", "8bd5ca"),
            FromPalette("catppuccin-frappe", "303446", "414559", "51576d", "626880", "c6d0f5", "737994", "ca9ee6", "e78284", "a6d189", "e5c890", "8caaee", "ca9ee6", "81c8be"),
            FromPalette("catppuccin-latte",  "eff1f5", "e6e9ef", "dce0e8", "bcc0cc", "4c4f69", "8c8fa1", "1e66f5", "d20f39", "40a02b", "df8e1d", "1e66f5", "8839ef", "179299", offersTransparency: false),
            FromPalette("tomorrow-night",    "1d1f21", "282a2e", "373b41", "373b41", "c5c8c6", "969896", "81a2be", "cc6666", "b5bd68", "f0c674", "81a2be", "b294bb", "8abeb7"),
            FromPalette("material",          "263238", "2e3c43", "314549", "425b67", "eeffff", "546e7a", "82aaff", "f07178", "c3e88d", "ffcb6b", "82aaff", "c792ea", "89ddff"),
            FromPalette("palenight",         "292d3e", "32374d", "444267", "444267", "a6accd", "676e95", "c792ea", "f07178", "c3e88d", "ffcb6b", "82aaff", "c792ea", "89ddff"),
            FromPalette("everforest-dark",   "2d353b", "343f44", "3d484d", "475258", "d3c6aa", "859289", "a7c080", "e67e80", "a7c080", "dbbc7f", "7fbbb3", "d699b6", "83c092"),
            FromPalette("everforest-light",  "fdf6e3", "f4f0d9", "efebd4", "e6e2cc", "5c6a72", "939f91", "8da101", "f85552", "8da101", "dfa000", "3a94c5", "df69ba", "35a77c", offersTransparency: false),
            FromPalette("rose-pine",         "191724", "1f1d2e", "26233a", "403d52", "e0def4", "908caa", "c4a7e7", "eb6f92", "31748f", "f6c177", "9ccfd8", "c4a7e7", "9ccfd8"),
            FromPalette("rose-pine-moon",    "232136", "2a273f", "393552", "44415a", "e0def4", "908caa", "c4a7e7", "eb6f92", "3e8fb0", "f6c177", "9ccfd8", "c4a7e7", "9ccfd8"),
            FromPalette("rose-pine-dawn",    "faf4ed", "fffaf3", "f2e9e1", "dfdad9", "575279", "9893a5", "907aa9", "b4637a", "286983", "ea9d34", "56949f", "907aa9", "56949f", offersTransparency: false),
            FromPalette("kanagawa",          "1f1f28", "2a2a37", "363646", "2d4f67", "dcd7ba", "727169", "7e9cd8", "c34043", "76946a", "dca561", "7e9cd8", "957fb8", "7aa89f"),
            FromPalette("ayu-dark",          "0a0e14", "0d1016", "1c212b", "1d2433", "b3b1ad", "5c6773", "e6b450", "f07178", "c2d94c", "ffb454", "59c2ff", "d2a6ff", "95e6cb"),
            FromPalette("ayu-mirage",        "1f2430", "232834", "2d3340", "33415e", "cccac2", "5c6773", "ffcc66", "f28779", "bae67e", "ffd580", "73d0ff", "d4bfff", "95e6cb"),
            FromPalette("ayu-light",         "fafafa", "f3f4f5", "e7e8e9", "d1e4f4", "5c6166", "abb0b6", "fa8d3e", "f07171", "86b300", "f2ae49", "399ee6", "a37acc", "4cbf99", offersTransparency: false),
            FromPalette("zenburn",           "3f3f3f", "4f4f4f", "6f6f6f", "5f5f5f", "dcdccc", "7f9f7f", "f0dfaf", "cc9393", "7f9f7f", "f0dfaf", "8cd0d3", "dc8cc3", "93e0e3"),
            FromPalette("night-owl",         "011627", "0b2942", "1d3b53", "1d3b53", "d6deeb", "637777", "82aaff", "ef5350", "addb67", "ecc48d", "82aaff", "c792ea", "7fdbca"),
            FromPalette("oceanic-next",      "1b2b34", "22313a", "343d46", "4f5b66", "cdd3de", "65737e", "6699cc", "ec5f67", "99c794", "fac863", "6699cc", "c594c5", "5fb3b3"),
            FromPalette("cobalt2",           "193549", "1f4662", "15232d", "0d3a58", "ffffff", "8aa0ad", "ffc600", "ff628c", "3ad900", "ffc600", "0088ff", "fb94ff", "80fcff"),
        };

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
