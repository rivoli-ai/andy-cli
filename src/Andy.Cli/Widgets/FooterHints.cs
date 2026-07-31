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
        /// <param name="shellMode">
        /// True while the composer is in shell escape mode (issue #286). The footer then advertises
        /// how to LEAVE the mode and how to stop a command, because those are the two things a user
        /// who arrived there by accident needs, and Escape no longer means "quit" on an empty
        /// shell prompt.
        /// </param>
        public static (string key, string action)[] Build(
            bool promptHistoryMode, bool toolOutputExpanded, bool mouseOn, bool shellMode = false)
        {
            string toolHint = toolOutputExpanded ? "Collapse output" : "Expand output";
            string mouseHint = mouseOn ? "Mouse On" : "Mouse Off";

            if (shellMode)
            {
                return new[]
                {
                    ("!", "Shell mode"),
                    ("Enter", "Run command"),
                    ("Ctrl+C", "Cancel command"),
                    ("ESC", "Leave shell mode"),
                    ("/attach", "Send output to model"),
                };
            }

            if (promptHistoryMode)
            {
                return new[]
                {
                    ("Ctrl+]", "Feed Mode"),
                    ("↑/↓", "Navigate"),
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
                ("ESC", "Quit"),
                ("", "http://localhost:5555"),
            };
        }
    }
}
