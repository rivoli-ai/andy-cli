namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Builds the ordered (key, action) hints shown in the footer <see cref="KeyHintsBar"/>.
    /// Includes a live "Mouse On/Off" indicator (toggled with F3) instead of the old debug-only
    /// "[F2] Toggle HUD" hint; F2 still toggles the HUD, it just isn't advertised in the footer.
    /// Mouse capture is off by default (so native text selection / Cmd+C keep working), so the
    /// indicator normally reads "Mouse Off" until the user presses F3.
    /// </summary>
    public static class FooterHints
    {
        public static (string key, string action)[] Build(
            bool promptHistoryMode, bool toolOutputExpanded, bool mouseOn, bool thinkingVisible = true)
        {
            string toolHint = toolOutputExpanded ? "Collapse output" : "Expand output";
            string mouseHint = mouseOn ? "Mouse On" : "Mouse Off";
            string thinkHint = thinkingVisible ? "Thinking On" : "Thinking Off";

            if (promptHistoryMode)
            {
                return new[]
                {
                    ("Ctrl+]", "Feed Mode"),
                    ("\u2191/\u2193", "Navigate"),
                    ("PgUp/PgDn", "Scroll"),
                    ("Ctrl+O", toolHint),
                    ("F3", mouseHint),
                    ("ESC", "Quit"),
                };
            }

            return new[]
            {
                ("Ctrl+P", "Commands"),
                ("PgUp/PgDn", "Scroll"),
                ("Ctrl+O", toolHint),
                ("F3", mouseHint),
                ("F4", thinkHint),
                ("ESC", "Quit"),
                ("", "http://localhost:5555"),
            };
        }
    }
}
