namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Shared, compact token-count formatting used by the context status bar and the
    /// live processing indicator so both surfaces render counts identically.
    /// </summary>
    public static class TokenFormat
    {
        /// <summary>
        /// Format a token count with a K/M suffix (e.g. 1500 -> "1.5K", 2_000_000 -> "2.0M").
        /// Counts below 1000 are rendered as-is.
        /// </summary>
        public static string Short(int tokens)
        {
            if (tokens >= 1_000_000)
                return $"{tokens / 1_000_000.0:F1}M";
            if (tokens >= 1_000)
                return $"{tokens / 1_000.0:F1}K";
            return tokens.ToString();
        }
    }
}
